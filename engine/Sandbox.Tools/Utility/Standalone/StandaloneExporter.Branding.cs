using System;
using System.IO;

namespace Editor;

partial class StandaloneExporter
{
	async Task QueueStartupBranding( string baseDir )
	{
		StandaloneManifest.StartupBranding.Clear();
		foreach ( var entry in _exportConfig.StartupBranding ?? [] )
		{
			var asset = AssetSystem.FindByPath( entry.Path );
			var source = asset?.AbsolutePath ?? (Path.IsPathRooted( entry.Path ) ? entry.Path : Path.Combine( Project.GetAssetsPath(), entry.Path ));
			var path = $"materials/startup_branding/{StandaloneManifest.StartupBranding.Count}{(entry.IsVideo ? Path.GetExtension( source ).ToLowerInvariant() : ".png")}";
			var destination = Path.Combine( baseDir, "core", path );
			if ( entry.IsVideo )
			{
				if ( !File.Exists( source ) ) throw new FileNotFoundException( "Startup video not found", source );
				QueueCopy( source, destination, BuildStep.CopyMisc );
				if ( StandaloneManifest.StartupBranding.Count == 0 )
				{
					var temporary = Path.Combine( Path.GetTempPath(), $"sbox-startup-{Guid.NewGuid():N}.png" );
					_temporarySources.Add( temporary );
					File.WriteAllBytes( temporary, await CreateStartupVideoPreview( source ) );
					QueueCopy( temporary, Path.Combine( baseDir, "core", StartupBranding.PreviewPath ), BuildStep.CopyMisc );
				}
			}
			else if ( Path.GetExtension( source ).Equals( ".vtex", StringComparison.OrdinalIgnoreCase ) )
			{
				QueueStartupImagePng( asset?.Path ?? entry.Path, destination );
			}
			else
			{
				using var bitmap = Bitmap.CreateFromBytes( File.ReadAllBytes( source ) );
				if ( bitmap is null || !bitmap.IsValid ) throw new InvalidOperationException( $"Couldn't read startup image '{entry.Path}'" );
				var temporary = Path.Combine( Path.GetTempPath(), $"sbox-startup-{Guid.NewGuid():N}.png" );
				_temporarySources.Add( temporary );
				File.WriteAllBytes( temporary, bitmap.ToPng() );
				QueueCopy( temporary, destination, BuildStep.CopyMisc );
			}
			StandaloneManifest.StartupBranding.Add( new StartupBranding { Path = path, Duration = entry.Duration, FadeIn = entry.FadeIn, FadeOut = entry.FadeOut } );
		}
	}

	internal static async Task<byte[]> CreateStartupVideoPreview( string source )
	{
		if ( !File.Exists( source ) ) throw new FileNotFoundException( "Startup video not found", source );
		using var player = new VideoPlayer();
		player.SetVideoOnly();
		player.Repeat = false;
		var fileSystem = new LocalFileSystem( Path.GetDirectoryName( Path.GetFullPath( source ) ) );
		player.Play( fileSystem, Path.GetFileName( source ) );
		var started = System.Diagnostics.Stopwatch.StartNew();
		while ( !player.Texture.IsLoaded )
		{
			player.Present();
			if ( started.Elapsed.TotalSeconds > 15 )
				throw new TimeoutException( $"Couldn't decode the first frame of startup video '{source}'" );
			if ( !player.Texture.IsLoaded ) await Task.Delay( 10 );
		}
		using var bitmap = player.Texture.GetBitmap();
		return bitmap.ToPng();
	}
}
