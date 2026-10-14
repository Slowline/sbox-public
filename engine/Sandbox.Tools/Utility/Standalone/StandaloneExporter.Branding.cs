using System;
using System.IO;

namespace Editor;

partial class StandaloneExporter
{
	void QueueStartupBranding( string baseDir )
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
}
