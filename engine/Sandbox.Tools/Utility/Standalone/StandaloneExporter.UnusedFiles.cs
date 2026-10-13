using System;
using System.IO;

namespace Editor;

partial class StandaloneExporter
{
	/// <summary>Files omitted by the optional reference analysis.</summary>
	public IReadOnlyList<ProjectPublisher.ProjectFile> ExcludedFiles => Array.AsReadOnly( _unusedFiles );

	/// <summary>Whether unknown file reads forced the analysis to keep every file.</summary>
	public bool HasUnresolvedFileAccess { get; private set; }

	/// <summary>Whether code references conservatively include resource/file patterns.</summary>
	public bool HasWildcardCodeReferences { get; private set; }

	void FilterUnusedFiles()
	{
		Logger.Info( "Analyzing unused export files.." );
		var files = PackageManifest.Assets.Where( x => !x.Skip ).ToArray();
		var byName = files.ToDictionary( x => UnusedExportFiles.Normalize( x.Name ), StringComparer.OrdinalIgnoreCase );
		var projects = LibrarySystem.All.Select( x => x.Project ).Prepend( Project ).Distinct().ToArray();
		var references = new HashSet<string>( UnusedExportFiles.TextReferences( Project.Config.ToJson() ), StringComparer.OrdinalIgnoreCase );
		if ( !string.IsNullOrWhiteSpace( _exportConfig.StartupImage ) && AssetSystem.FindByPath( _exportConfig.StartupImage ) is { } startupImage )
			references.Add( startupImage.Path );
		foreach ( var project in projects.Where( x => x.HasCodePath() ) )
		{
			foreach ( var source in Directory.EnumerateFiles( project.GetCodePath(), "*", SearchOption.AllDirectories ) )
			{
				var relative = Path.GetRelativePath( project.GetCodePath(), source ).Replace( '\\', '/' );
				if ( relative.Split( '/' ).Any( x => x is "obj" or "bin" || x.StartsWith( '.' ) ) ) continue;
				if ( Path.GetExtension( source ) is not (".cs" or ".razor") ) continue;
				references.UnionWith( UnusedExportFiles.CodeReferences( File.ReadAllText( source ) ) );
			}
		}
		HasUnresolvedFileAccess = references.Contains( "*" );
		HasWildcardCodeReferences = references.Any( x => x.Contains( '*' ) );
		if ( HasUnresolvedFileAccess )
			Logger.Info( "Unresolved dynamic file access found; retaining all export files" );

		var explicitResources = (Project.Config.Resources ?? "").Split( '\n', StringSplitOptions.RemoveEmptyEntries )
			.Select( x => x.Trim() ).Where( x => !x.StartsWith( "//" ) );
		var projectFolders = projects.SelectMany( x => new[] { x.HasCodePath() ? x.GetCodePath() : null, x.HasAssetsPath() ? x.GetAssetsPath() : null } )
			.Where( x => x is not null ).Select( x => Path.GetFullPath( x ).TrimEnd( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar ) + Path.DirectorySeparatorChar ).ToArray();
		var roots = files.Where( x => (x.Contents is not null && !x.Name.EndsWith( ".t.png", StringComparison.OrdinalIgnoreCase )) || x.AbsolutePath is null && x.Contents is null ||
			(x.AbsolutePath is not null && !projectFolders.Any( p => Path.GetFullPath( x.AbsolutePath ).StartsWith( p, StringComparison.OrdinalIgnoreCase ) )) ||
			x.Name.EndsWith( ".scene_c", StringComparison.OrdinalIgnoreCase ) || x.Name.EndsWith( ".vpk", StringComparison.OrdinalIgnoreCase ) ||
			x.Name.EndsWith( ".surface_c", StringComparison.OrdinalIgnoreCase ) ||
			Path.GetExtension( x.Name ).ToLowerInvariant() is ".scss" or ".css" ||
			new[] { "projectsettings/", "localization/", "fonts/", ".bin/" }.Any( p => UnusedExportFiles.Normalize( x.Name ).StartsWith( p ) ) )
			.Select( x => x.Name ).Concat( explicitResources ).ToArray();

		IEnumerable<string> Dependencies( string name )
		{
			var file = byName[name];
			yield return name + ".t.png";
			var asset = AssetSystem.FindByPath( file.AbsolutePath ?? file.Name );
			if ( asset is not null )
			{
				foreach ( var reference in asset.GetReferences( false ).Prepend( asset ) )
				{
					yield return reference.Path;
					foreach ( var additional in reference.GetAdditionalGameFiles() ) yield return additional;
					foreach ( var content in reference.GetAdditionalContentFiles().Where( x => x.EndsWith( ".rect", StringComparison.OrdinalIgnoreCase ) ) )
						if ( AssetSystem.FindByPath( content ) is { } additionalAsset ) yield return additionalAsset.RelativePath;
				}
			}
			if ( file.AbsolutePath is not null && Path.GetExtension( name ) is ".json" or ".txt" or ".css" or ".scss" or ".html" or ".xml" )
				foreach ( var reference in UnusedExportFiles.TextReferences( File.ReadAllText( file.AbsolutePath ) ) )
				{
					yield return reference;
					if ( reference.Contains( ':' ) || Path.IsPathRooted( reference ) ) continue;
					var relative = Path.GetRelativePath( "/", Path.GetFullPath( Path.Combine( "/", Path.GetDirectoryName( name ) ?? "", reference ) ) );
					yield return relative;
				}
		}

		var used = UnusedExportFiles.FindUsed( files.Select( x => x.Name ), roots, references, Dependencies );
		var omitted = _unusedFiles = files.Where( x => !used.Contains( UnusedExportFiles.Normalize( x.Name ) ) ).ToArray();
		foreach ( var file in omitted )
		{
			PackageManifest.Assets.Remove( file );
			Logger.Info( $"Excluded unused file: {file.Name} ({file.Size:n0} bytes; no discovered reference)" );
		}
		Logger.Info( $"Unused-file analysis excluded {omitted.Length:n0} files ({omitted.Sum( x => (long)x.Size ):n0} bytes)" );
	}
}
