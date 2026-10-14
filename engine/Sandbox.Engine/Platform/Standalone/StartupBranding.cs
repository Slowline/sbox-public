namespace Sandbox;

/// <summary>A single image or video in a standalone game's startup sequence.</summary>
[Expose]
public class StartupBranding
{
	[KeyProperty, FilePath( Extension = "vtex,png,jpg,jpeg,tga,bmp,webp,mp4,webm,mov,avi,wmv,mkv,m4v" )]
	public string Path { get; set; }

	[Title( "Image Duration (seconds)" ), Description( "Time at full opacity, excluding fades. Videos play once in full." ), Range( 0, 120 )]
	public float Duration { get; set; } = 3;

	[Title( "Fade In (seconds)" ), Range( 0, 10 )]
	public float FadeIn { get; set; } = 0.5f;

	[Title( "Fade Out (seconds)" ), Range( 0, 10 )]
	public float FadeOut { get; set; } = 0.5f;

	internal bool IsVideo => System.IO.Path.GetExtension( Path ?? "" ).ToLowerInvariant() is ".mp4" or ".webm" or ".mov" or ".avi" or ".wmv" or ".mkv" or ".m4v";

	internal void Validate()
	{
		if ( string.IsNullOrWhiteSpace( Path ) || (!IsVideo && System.IO.Path.GetExtension( Path ).ToLowerInvariant() is not (".vtex" or ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".webp")) )
			throw new ArgumentException( $"Unsupported startup branding asset '{Path}'" );
		if ( !float.IsFinite( Duration + FadeIn + FadeOut ) || Duration < 0 || FadeIn < 0 || FadeOut < 0 )
			throw new ArgumentException( "Startup branding timings must be finite and non-negative" );
	}

	internal float Opacity( float elapsed, float duration ) => Math.Clamp( Math.Min(
		FadeIn > 0 ? elapsed / FadeIn : 1,
		FadeOut > 0 ? (duration - elapsed) / FadeOut : 1 ), 0, 1 );
}
