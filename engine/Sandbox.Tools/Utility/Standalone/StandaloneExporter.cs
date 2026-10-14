using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using static Editor.ProjectPublisher;

namespace Editor;

public partial class StandaloneExporter
{
	static Logger Logger = new Logger( "Exporter" );

	public Project Project => _exportConfig.Project;
	public IReadOnlyList<QueuedFile> Files => _files.AsReadOnly();

	public PackageManifest PackageManifest { get; protected set; }
	public StandaloneManifest StandaloneManifest { get; protected set; }

	public Action<ExportProgress> OnProgressChanged { get; set; }

	private List<QueuedFile> _files = new();
	private readonly List<string> _temporarySources = new();
	private ExportConfig _exportConfig;
	private string _packedAssetsFolder;
	private ProjectFile[] _unusedFiles = [];

	StandaloneExporter( ExportConfig config )
	{
		StandaloneManifest = new StandaloneManifest();
		PackageManifest = new PackageManifest();
		_exportConfig = config;
	}

	async Task GenerateAssetManifest( IProgress progress = null, CancellationToken cancel = default )
	{
		await PackageManifest.BuildFromAssets( _exportConfig.Project, progress, cancel );
	}

	public static async Task<StandaloneExporter> FromConfig( ExportConfig config )
	{
		var p = new StandaloneExporter( config );
		await p.GenerateAssetManifest();
		return p;
	}

	private void QueueCopyFile( string targetDir, ProjectFile file, BuildStep type )
	{
		var fileName = file.Name;
		var targetPath = Path.Combine( targetDir, fileName );

		//
		// Create directory if it doesn't exist
		//
		{
			var targetSubDir = Path.GetDirectoryName( targetPath );
			Directory.CreateDirectory( targetSubDir );
		}

		if ( file.Contents is not null )
		{
			if ( File.Exists( targetPath ) ) File.Delete( targetPath );
			File.WriteAllBytes( targetPath, file.Contents );
		}
		else if ( file.AbsolutePath is not null )
		{
			var sourcePath = file.AbsolutePath;
			QueueCopy( sourcePath, targetPath, type );
		}
		else
		{
			Log.Warning( $"Unable to copy {file.Name} - has no content defined!" );
		}

		file.Skip = true;
	}

	public async Task Run()
	{
		Logger.Info( $"Exporting {Project.Config.Title} to {_exportConfig.TargetDir}" );

		ValidateExecutableName( _exportConfig.ExecutableName );
		ParseExecutableVersion( _exportConfig.ExecutableVersion );
		foreach ( var branding in _exportConfig.StartupBranding ?? [] )
		{
			if ( branding is null ) throw new ArgumentException( "Startup branding entries cannot be empty" );
			branding.Validate();
		}
		if ( _exportConfig.PackAssets || _exportConfig.ExcludeUnusedFiles )
		{
			foreach ( var file in PackageManifest.Assets.Where( x => x.AbsolutePath is not null ) )
			{
				var target = Path.GetFullPath( Path.Combine( _exportConfig.TargetDir, Standalone.GamePath, file.Name ) );
				if ( string.Equals( target, Path.GetFullPath( file.AbsolutePath ), StringComparison.OrdinalIgnoreCase ) )
					throw new InvalidOperationException( "Filtered or VPK exports must not overwrite the project's source assets" );
			}
		}

		Logger.Info( $"Compiling assemblies.." );
		await Compile();

		try
		{
			Logger.Info( "Building manifest.." );
			await BuildManifest();
			Logger.Info( "Writing executable.." );
			WriteExecutable();
			Logger.Info( "Copying files.." );
			await CopyAllFiles();
			if ( StandaloneManifest.StartupBranding.Any( x => _files.Any( f => f.Destination == Path.Combine( _exportConfig.TargetDir, "core", x.Path ) && f.State != QueuedFileState.Copied ) ) )
				throw new IOException( "Couldn't finish export because startup branding files failed to copy" );
			if ( _exportConfig.ExcludeUnusedFiles && _files.Any( x => x.Step == BuildStep.CopyProjectAssets && x.State != QueuedFileState.Copied ) )
				throw new IOException( "Couldn't finish filtered export because some project files failed to copy" );
			if ( _exportConfig.PackAssets )
			{
				if ( _files.Any( x => x.Step == BuildStep.CopyProjectAssets && x.State != QueuedFileState.Copied ) )
					throw new IOException( "Couldn't pack assets because some export files failed to copy" );
				Logger.Info( "Packing game assets into VPK.." );
				var archiveFolder = Path.Combine( Path.GetDirectoryName( _packedAssetsFolder ), "archive" );
				Directory.CreateDirectory( archiveFolder );
				await VpkArchive.WriteAsync( _packedAssetsFolder, Path.Combine( archiveFolder, Standalone.AssetsArchiveName ) );
				var output = Path.Combine( _exportConfig.TargetDir, Standalone.GamePath );
				Directory.CreateDirectory( output );
				foreach ( var file in Directory.EnumerateFiles( archiveFolder ) )
					File.Move( file, Path.Combine( output, Path.GetFileName( file ) ), true );
				// shortcut: remove only this export's loose files; use a clean target to discard obsolete assets from older exports.
				foreach ( var file in Directory.EnumerateFiles( _packedAssetsFolder, "*", SearchOption.AllDirectories ) )
				{
					var loose = Path.Combine( output, Path.GetRelativePath( _packedAssetsFolder, file ) );
					if ( File.Exists( loose ) ) File.Delete( loose );
				}
			}
			var assetsRoot = Path.GetFullPath( Path.Combine( _exportConfig.TargetDir, Standalone.GamePath ) ).TrimEnd( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar ) + Path.DirectorySeparatorChar;
			foreach ( var file in _unusedFiles )
			{
				var previous = Path.GetFullPath( Path.Combine( assetsRoot, file.Name ) );
				if ( !previous.StartsWith( assetsRoot, StringComparison.OrdinalIgnoreCase ) )
					throw new InvalidOperationException( $"Unused file '{file.Name}' resolves outside the export assets directory: '{previous}' outside '{assetsRoot}'" );
				if ( File.Exists( previous ) ) File.Delete( previous );
			}
		}
		finally
		{
			foreach ( var source in _temporarySources )
			{
				try { File.Delete( source ); }
				catch ( Exception exception ) { Logger.Warning( exception, $"Couldn't remove temporary export file {source}" ); }
			}
			_temporarySources.Clear();
			if ( _packedAssetsFolder is not null ) Directory.Delete( Path.GetDirectoryName( _packedAssetsFolder ), true );
		}

		Logger.Info( $"Export complete!" );
	}

	private async Task BuildManifest()
	{
		// The compiled game assemblies, into assets/.bin
		if ( _exportConfig.AssemblyFiles is not null )
		{
			foreach ( var f in _exportConfig.AssemblyFiles )
			{
				if ( f.Value is byte[] bytes )
				{
					await AddFile( bytes, f.Key );
				}
			}
		}

		// Add assets from Cloud.Model( .. ) etc
		foreach ( var package in _exportConfig.CodePackages )
		{
			await AddCodePackageReference( package );
		}

		// Make sure we're copying into a directory that exists
		Directory.CreateDirectory( _exportConfig.TargetDir );

		// Create standalone properties - these go into the executable's resources, see WriteExecutable
		StandaloneManifest = CreateStandaloneManifest( _exportConfig.TargetDir );

		// Build queue
		if ( _exportConfig.ExcludeUnusedFiles )
			FilterUnusedFiles();
		QueueAddonFiles( _exportConfig.TargetDir, BuildStep.CopyProjectAssets );
		QueueBaseFiles( _exportConfig.TargetDir );
	}

	private async Task CopyAllFiles()
	{
		var orderedFiles = _files.OrderBy( x => x.Step );
		foreach ( var f in orderedFiles )
		{
			await CopyFileAsync( f );
		}
	}


	private StandaloneManifest CreateStandaloneManifest( string baseDir )
	{
		if ( !_exportConfig.Project.Config.TryGetMeta( "ControlModes", out ControlModeSettings controlSettings ) )
			controlSettings = new();

		return new StandaloneManifest()
		{
			BuildDate = _exportConfig.BuildDate,
			ExecutableName = _exportConfig.ExecutableName,
			Ident = _exportConfig.Project.Package.Ident,
			Name = _exportConfig.Project.Package.Title,
			AppId = _exportConfig.AppId,

			IsVRProject = controlSettings.IsVROnly,
			PackAssets = _exportConfig.PackAssets
		};
	}

	private void QueueAddonFiles( string baseDir, BuildStep type )
	{
		var targetDir = Path.Combine( baseDir, Standalone.GamePath );
		if ( _exportConfig.PackAssets )
		{
			_packedAssetsFolder = Path.Combine( Path.GetTempPath(), $"sbox-vpk-{Guid.NewGuid():N}", "assets" );
			targetDir = _packedAssetsFolder;
		}
		Directory.CreateDirectory( targetDir );

		var targets = PackageManifest.Assets.Where( x => !x.Skip ).ToArray();
		var tasks = new List<Task>();

		foreach ( var t in targets )
		{
			if ( _exportConfig.PackAssets && t.Name.EndsWith( ".vpk", StringComparison.OrdinalIgnoreCase ) &&
				VpkArchive.IsArchiveFile( Path.Combine( baseDir, Standalone.GamePath, Standalone.AssetsArchiveName ), Path.Combine( baseDir, Standalone.GamePath, t.Name ) ) )
				throw new InvalidOperationException( $"Asset '{t.Name}' conflicts with the standalone assets archive name" );

			var directory = _exportConfig.PackAssets && t.Name.EndsWith( ".vpk", StringComparison.OrdinalIgnoreCase )
				? Path.Combine( baseDir, Standalone.GamePath ) : targetDir;
			QueueCopyFile( directory, t, type );
		}
	}

	private void QueueBaseFiles( string baseDir )
	{
		var engineDir = Environment.CurrentDirectory;

		void QueueFrom( IEnumerable<string> paths )
		{
			foreach ( var file in paths )
			{
				var relativePath = Path.GetRelativePath( engineDir, file );
				var targetPath = Path.Combine( baseDir, relativePath );

				QueueCopy( file, targetPath, BuildStep.CopyCoreAssets );
			}
		}

		{
			// Get all core files - only the ones we absolutely need, because everything else should
			// already have been copied into the addon itself.
			// This is mainly stuff like dev textures that are necessary for the engine to run.
			var coreFiles = GetCoreFiles( engineDir );
			QueueFrom( coreFiles );

			foreach ( var resource in GetSurfaceReferences( coreFiles ) )
			{
				QueueCopy( resource.AbsolutePath, Path.Combine( baseDir, "core", resource.RelativePath ), BuildStep.CopyCoreAssets );
			}

			// Fetch all the DLLs we need to run the engine
			QueueFrom( GetDllFiles( engineDir ) );
		}

		//
		// Fetch code resources (resource manifest stuff from native)
		//
		{
			var codeResources = GetCodeResources( engineDir );

			foreach ( var resource in codeResources )
			{
				var sourcePath = resource.AbsolutePath;
				var targetPath = Path.Combine( baseDir, $"core/{resource.RelativePath}" );

				// Only copy compiled resources
				if ( !sourcePath.EndsWith( "_c" ) )
					sourcePath += "_c";

				if ( !targetPath.EndsWith( "_c" ) )
					targetPath += "_c";

				QueueCopy( sourcePath, targetPath, BuildStep.CopyCode );
			}
		}

		//
		// Copy loose files
		//
		{
			// Have to copy this or the engine will crash super early. Not sure we actually use it though
			QueueCopy( $"{engineDir}/bin/assettypes.txt", $"{baseDir}/bin/assettypes.txt", BuildStep.CopyMisc );
		}

		//
		// Splash screen image
		// Has to be part of core (maybe we can change this)
		//
		QueueStartupBranding( baseDir );
		if ( _exportConfig.StartupBranding is null && !string.IsNullOrEmpty( _exportConfig.StartupImage ) )
		{
			var path = _exportConfig.StartupImage;
			var startupImagePath = Path.Combine( _exportConfig.Project.GetAssetsPath(), path + "_c" );

			// The image picker stores the file's absolute path, but the resource system only loads asset-relative paths
			if ( Path.IsPathRooted( path ) && AssetSystem.FindByPath( path ) is { } startupAsset )
			{
				path = startupAsset.Path;
				startupImagePath = startupAsset.GetCompiledFile( true ) ?? startupImagePath;
			}

			QueueCopy( startupImagePath, $"{baseDir}/core/materials/startup_background.vtex_c", BuildStep.CopyMisc );
			QueueStartupImagePng( path, $"{baseDir}/core/materials/startup_background_color.png" );
		}
	}

	private void QueueStartupImagePng( string resourcePath, string destination )
	{
		var texture = Texture.Load( resourcePath, false );
		if ( texture is null || !texture.IsValid || texture.IsError )
			throw new InvalidOperationException( $"Couldn't load selected startup image '{resourcePath}'" );

		using var bitmap = texture.GetBitmap();
		if ( bitmap is null || !bitmap.IsValid )
			throw new InvalidOperationException( $"Couldn't read selected startup image '{resourcePath}'" );

		var desc = g_pRenderDevice.GetTextureDesc( texture.native );
		var width = desc.m_nDisplayRectWidth > 0 ? Math.Min( desc.m_nDisplayRectWidth, bitmap.Width ) : bitmap.Width;
		var height = desc.m_nDisplayRectHeight > 0 ? Math.Min( desc.m_nDisplayRectHeight, bitmap.Height ) : bitmap.Height;
		using var image = bitmap.Crop( new Rect( 0, 0, width, height ) );

		var source = Path.Combine( Path.GetTempPath(), $"sbox-startup-{Guid.NewGuid():N}.png" );
		try
		{
			File.WriteAllBytes( source, image.ToPng() );
			_temporarySources.Add( source );
			QueueCopy( source, destination, BuildStep.CopyMisc );
		}
		catch
		{
			File.Delete( source );
			throw;
		}
	}

	private void QueueCopy( string source, string dest, BuildStep type )
	{
		_files.Add( new QueuedFile( source, dest, type ) );
	}

	private async Task CopyFileAsync( QueuedFile file )
	{
		//
		// Make sure we have a directory to copy into
		//
		{
			var targetDir = Path.GetDirectoryName( file.Destination );
			if ( !Directory.Exists( targetDir ) ) Directory.CreateDirectory( targetDir );
		}

		//
		// Attempt to copy the file
		//
		try
		{
			// Check if the file exists first - there's no point in spinning up a task if it doesn't.
			if ( !File.Exists( file.Source ) )
			{
				file.State = QueuedFileState.FileNotFound;
				return;
			}

			await Task.Run( () =>
			{
				try
				{
					File.Copy( file.Source, file.Destination, true );
					file.State = QueuedFileState.Copied;
				}
				catch ( Exception ex )
				{
					Log.Error( ex );
					file.State = QueuedFileState.FailedToCopy;
				}
			} );
		}
		finally
		{
			TriggerProgessChanged( file );
		}
	}

	/// <summary>
	/// Manually add a file to the manifest
	/// </summary>
	public Task AddFile( byte[] contents, string relativePath )
	{
		return PackageManifest.AddFile( contents, relativePath );
	}

	/// <summary>
	/// Manually add a file to the manifest
	/// </summary>
	public Task AddFile( string contents, string relativePath )
	{
		return PackageManifest.AddTextFile( contents, relativePath );
	}

	/// <summary>
	/// If the code is referencing a package - we can add it to the manifest using this.
	/// </summary>
	public Task AddCodePackageReference( string package )
	{
		return PackageManifest.AddCodePackageReference( package );
	}

	private void TriggerProgessChanged( QueuedFile file )
	{
		if ( OnProgressChanged is not null )
		{
			var extension = Path.GetExtension( file.Source );
			var assetType = AssetType.FromExtension( extension );

			var filesTotal = Files.Count;
			var filesDone = Files.Count( x => x.State != QueuedFileState.Waiting );
			var filesLeft = Files.Count( x => x.State == QueuedFileState.Waiting );

			var progressFraction = (float)(filesTotal - filesLeft) / filesTotal;
			var actionText = $"Copying files ({(progressFraction * 100f).CeilToInt()}%)";
			var stepString = file.Step.GetDescription();

			var buildIssues = new string[Files.Count( x => x.State != QueuedFileState.Waiting && x.State != QueuedFileState.Copied )];
			var issueIdx = 0;

			for ( int fileIdx = 0; fileIdx < Files.Count; fileIdx++ )
			{
				var processedFile = Files[fileIdx];

				if ( processedFile.State == QueuedFileState.Waiting )
					continue;

				if ( processedFile.State == QueuedFileState.Copied )
					continue;

				if ( processedFile.State == QueuedFileState.FailedToCopy )
					buildIssues[issueIdx] = $"Failed to copy '{processedFile.Source}' during step '{processedFile.Step.GetDescription()}'";

				if ( processedFile.State == QueuedFileState.FileNotFound )
					buildIssues[issueIdx] = $"File '{processedFile.Source}' not found during step '{processedFile.Step.GetDescription()}'";

				issueIdx++;
			}

			var progress = new ExportProgress()
			{
				ProgressFraction = progressFraction,
				CurrentOperation = stepString,
				FilesDone = filesDone,
				FilesTotal = filesTotal,
				BuildIssues = buildIssues
			};

			UpdateProgress( progress );
		}
	}
}
