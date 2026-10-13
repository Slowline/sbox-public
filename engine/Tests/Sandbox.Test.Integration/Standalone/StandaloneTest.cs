using Editor;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace StandaloneTests;

/// <summary>
/// Tests for the Standalone export pipeline
/// </summary>
[TestClass]
public class StandaloneTest
{
	[TestMethod]
	public void ExportPresetsKeepPackingOption()
	{
		var saved = System.Text.Json.JsonSerializer.Serialize( new ExportConfig { PackAssets = true } );
		Assert.IsTrue( System.Text.Json.JsonSerializer.Deserialize<ExportConfig>( saved ).PackAssets );
		Assert.IsFalse( System.Text.Json.JsonSerializer.Deserialize<ExportConfig>( "{}" ).PackAssets, "Older presets should keep exporting loose assets" );
	}

	[TestInitialize]
	public void TestInitialize()
	{
		Project.Clear();
	}

	[TestMethod]
	public async Task PackedExportRejectsSourceAssetsAsTarget()
	{
		var project = Project.AddFromFile( "unittest/addons/spacewars/.sbproj" );
		var config = new ExportConfig { Project = project, TargetDir = project.GetRootPath(), ExecutableName = "test", PackAssets = true };
		var exporter = await StandaloneExporter.FromConfig( config );
		await Assert.ThrowsExceptionAsync<InvalidOperationException>( () => exporter.Run() );
	}

	[TestCleanup]
	public void TestCleanup()
	{
		Project.Clear();
	}

	[TestMethod]
	[DataRow( false, false )]
	[DataRow( true, false )]
	[DataRow( false, true )]
	[DataRow( true, true )]
	public async Task ExportAndRun( bool packAssets, bool filterUnusedFiles )
	{
		var config = new ExportConfig();
		var tempFolderName = Guid.NewGuid().ToString();
		config.TargetDir = Path.Combine( Path.GetTempPath(), tempFolderName, "sboxexportest" );
		config.AppId = 480; // SpaceWar AppID
		config.PackAssets = packAssets;
		config.ExcludeUnusedFiles = filterUnusedFiles;
		if ( packAssets ) config.ExecutableVersion = "2.3.4.5";
		config.TargetIcon = "core/tools/images/hammer/appicon.ico"; // the wizard always offers one, so exercise the icon path
		string sourceDocumentation = null;
		var documentationName = $"unused_export_test_{Guid.NewGuid():N}.md";

		try
		{
			var project = Project.AddFromFile( "unittest/addons/spacewars/.sbproj" );
			config.Project = project;
			config.ExecutableName = project.Package.Ident;
			project.Config.Resources = "start.scene_c";
			sourceDocumentation = Path.Combine( project.GetCodePath(), documentationName );
			File.WriteAllText( sourceDocumentation, "Unreferenced developer documentation" );

			CleanOutput( config );
			var previousAssets = Path.Combine( config.TargetDir, Standalone.GamePath );
			Directory.CreateDirectory( previousAssets );
			File.WriteAllText( Path.Combine( previousAssets, documentationName ), "Previous unfiltered export" );
			if ( packAssets )
			{
				var oldAssets = Path.Combine( config.TargetDir, Standalone.GamePath );
				Directory.CreateDirectory( oldAssets );
				File.WriteAllText( Path.Combine( oldAssets, "start.scene_c" ), "Previous loose export" );
			}

			//
			// Build the project
			//
			{
				var exporter = await StandaloneExporter.FromConfig( config );
				await exporter.AddFile( File.ReadAllBytes( "core/maps/error.vpk" ), "maps/export_test.vpk" );
				await exporter.Run();
			}

			//
			// The game is the standalone launcher's apphost with the game's identity in its PE
			// resources; the launcher assembly lives in bin/managed with the rest of the engine.
			// Nothing about the game's identity is a loose file in assets/.
			//
			{
				var executable = Path.Combine( config.TargetDir, $"{config.ExecutableName}.exe" );
				Assert.IsTrue( File.Exists( executable ), "Export is missing its executable" );

				foreach ( var file in new[] { "sbox-standalone.dll", "sbox-standalone.runtimeconfig.json" } )
				{
					Assert.IsTrue( File.Exists( Path.Combine( config.TargetDir, "bin", "managed", file ) ), $"Export is missing bin/managed/{file}" );
				}

				Assert.AreEqual( 1, Directory.GetFiles( config.TargetDir ).Length, $"Only the executable should sit in the export root, found: {string.Join( ", ", Directory.GetFiles( config.TargetDir ).Select( Path.GetFileName ) )}" );

				var assets = Path.Combine( config.TargetDir, Standalone.GamePath );
				Assert.IsTrue( File.Exists( Path.Combine( assets, "maps", "export_test.vpk" ) ), "Map VPKs must stay loose" );
				if ( packAssets ) Assert.IsFalse( new VpkArchive( Path.Combine( assets, Standalone.AssetsArchiveName ) ).Entries.ContainsKey( "/maps/export_test.vpk" ), "Map VPKs must not be nested inside the assets archive" );
				Assert.IsFalse( File.Exists( Path.Combine( assets, ".sbproj" ) ), "Export shouldn't have a loose .sbproj" );
				Assert.IsFalse( File.Exists( Path.Combine( assets, "standalone.manifest.json" ) ), "Export shouldn't have a loose manifest" );

				// Only compiled assemblies ship - never the code archive (the game's source) or doc xml
				var exportedFiles = packAssets ? (BaseFileSystem)new VpkFileSystem( Path.Combine( assets, Standalone.AssetsArchiveName ) ) : new LocalFileSystem( assets );
				string[] binFiles;
				try
				{
					binFiles = exportedFiles.FindFile( ".bin" ).ToArray();
					Assert.IsTrue( exportedFiles.FileExists( "start.scene_c" ), "Export is missing its startup scene" );
					Assert.AreEqual( !filterUnusedFiles, exportedFiles.FileExists( documentationName ), "Only the opt-in filter should omit unreferenced documentation" );
					if ( filterUnusedFiles ) Assert.IsFalse( File.Exists( Path.Combine( assets, documentationName ) ), "Filtered exports must also remove a previous loose copy" );
					if ( packAssets ) Assert.IsTrue( Directory.EnumerateFiles( assets, "*", SearchOption.AllDirectories ).All( x => x.EndsWith( ".vpk" ) ), "Packed assets should have no loose content" );
				}
				finally { exportedFiles.Dispose(); }
				Assert.IsTrue( binFiles.Length > 0, ".bin should contain the game's assemblies" );
				CollectionAssert.AreEquivalent( binFiles.Where( f => f.EndsWith( ".dll" ) ).ToArray(), binFiles, $".bin should only contain assemblies, has: {string.Join( ", ", binFiles )}" );

				// What Windows shows for the exe (Explorer, Defender, SmartScreen) is the game, not the template it was made from
				var versionInfo = FileVersionInfo.GetVersionInfo( executable );
				Assert.AreEqual( config.ExecutableVersion, versionInfo.FileVersion );
				Assert.AreEqual( config.ExecutableVersion, versionInfo.ProductVersion );
				var expectedVersion = Version.Parse( config.ExecutableVersion );
				var versionParts = (expectedVersion.Major, expectedVersion.Minor, expectedVersion.Build, expectedVersion.Revision);
				Assert.AreEqual( versionParts, (versionInfo.FileMajorPart, versionInfo.FileMinorPart, versionInfo.FileBuildPart, versionInfo.FilePrivatePart) );
				Assert.AreEqual( versionParts, (versionInfo.ProductMajorPart, versionInfo.ProductMinorPart, versionInfo.ProductBuildPart, versionInfo.ProductPrivatePart) );
				Assert.AreEqual( project.Config.Title, versionInfo.ProductName, "Executable's version info should name the game" );
				Assert.AreEqual( project.Config.Title, versionInfo.FileDescription, "Executable's version info should name the game" );
				Assert.IsFalse( (versionInfo.OriginalFilename ?? "").Contains( "sbox-standalone" ), "Executable still carries the template's version info" );

				// Resource names are UTF-16 in the PE resource directory, RCDATA contents are our UTF-8 JSON
				var image = File.ReadAllBytes( executable );
				foreach ( var resource in new[] { Standalone.ManifestResourceName, Standalone.ProjectConfigResourceName } )
				{
					Assert.IsTrue( image.AsSpan().IndexOf( System.Text.Encoding.Unicode.GetBytes( resource ) ) >= 0, $"Executable has no {resource} resource" );
				}
				Assert.IsTrue( image.AsSpan().IndexOf( System.Text.Encoding.UTF8.GetBytes( $"\"Ident\":\"{project.Config.Ident}\"" ) ) >= 0, "Executable's manifest doesn't name the game" );
			}

			//
			// Run the exported project, make sure it launches and exits cleanly
			//
			{
				using var process = new Process
				{
					StartInfo = new ProcessStartInfo
					{
						FileName = Path.Combine( config.TargetDir, $"{config.ExecutableName}.exe" ),
						WorkingDirectory = config.TargetDir,
						Arguments = "-headless -test-standalone",
						RedirectStandardError = true,
						RedirectStandardOutput = true,
						UseShellExecute = false,
						CreateNoWindow = true
					}
				};

				// forward error output so we know why it's failed
				process.ErrorDataReceived += ( sender, e ) =>
				{
					if ( e.Data != null )
						Console.Error.WriteLine( e.Data );
				};
				process.OutputDataReceived += ( sender, e ) =>
				{
					if ( e.Data is not null ) Console.WriteLine( e.Data );
				};

				bool success = process.Start();
				Assert.IsTrue( success, "Failed to start standalone exe" );

				process.BeginErrorReadLine();
				process.BeginOutputReadLine();

				// A hung exe would otherwise stall the whole serial suite - kill it after a generous timeout
				using var timeout = new CancellationTokenSource( TimeSpan.FromMinutes( 5 ) );

				try
				{
					await process.WaitForExitAsync( timeout.Token );
				}
				catch ( OperationCanceledException )
				{
					process.Kill( entireProcessTree: true );
					Assert.Fail( "Standalone exe didn't exit within 5 minutes - killed the process" );
				}

				int exitCode = process.ExitCode;
				Assert.AreEqual( 0, exitCode, $"Process exited with code {exitCode}" );
			}
		}
		finally
		{
			if ( sourceDocumentation is not null ) File.Delete( sourceDocumentation );
			CleanOutput( config );
		}
	}

	private void CleanOutput( ExportConfig config )
	{
		if ( config?.TargetDir is null || !Directory.Exists( config.TargetDir ) )
			return;

		try
		{
			Directory.Delete( config.TargetDir, true );
		}
		catch ( IOException )
		{
			Thread.Sleep( 500 );
			Directory.Delete( config.TargetDir, true );
		}
		catch ( UnauthorizedAccessException )
		{
			Thread.Sleep( 500 );
			Directory.Delete( config.TargetDir, true );
		}
	}

}
