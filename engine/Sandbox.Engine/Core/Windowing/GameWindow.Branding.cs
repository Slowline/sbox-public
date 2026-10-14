using System.Diagnostics;
using Sandbox.Utility;

namespace Sandbox.Engine;

internal sealed partial class GameWindow
{
	internal async Task PlayStartupBranding()
	{
		if ( !Application.IsStandalone || Application.IsHeadless || CommandLine.HasSwitch( "-nowindow" ) ) return;
		var entries = Standalone.Manifest?.StartupBranding;
		if ( entries is not { Count: > 0 } ) return;

		startupWindow?.Dispose();
		startupWindow = null;
		playingBranding = true;
		try
		{
			foreach ( var entry in entries )
			{
				if ( Application.WantsExit ) break;
				try { await PlayStartupBranding( entry ); }
				catch ( Exception e ) { Log.Warning( e, $"Couldn't play startup branding '{entry?.Path}'" ); }
			}
		}
		finally
		{
			playingBranding = false;
			brandingTexture = null;
		}
	}

	async Task PlayStartupBranding( StartupBranding entry )
	{
		entry.Validate();
		using var video = entry.IsVideo ? new VideoPlayer() : null;
		using var image = entry.IsVideo ? null : Bitmap.CreateFromBytes( EngineFileSystem.Root.ReadAllBytes( "core/" + entry.Path ).ToArray() );
		using var texture = video?.Texture ?? image?.ToTexture( mips: false );
		bool finished = false;
		if ( video is not null )
		{
			video.Repeat = false;
			video.OnFinished = () => finished = true;
			video.Play( EngineFileSystem.Root, "core/" + entry.Path );
		}
		brandingTexture = texture;
		var started = Stopwatch.GetTimestamp();
		var ready = false;
		var lastPlayback = 0f;
		var lastProgress = started;
		var lastFrame = started;
		var imageElapsed = 0f;
		try
		{
			while ( Handle != IntPtr.Zero && !Application.WantsExit )
			{
				SdlEvents.Poll();
				var now = Stopwatch.GetTimestamp();
				var frameTime = (float)Stopwatch.GetElapsedTime( lastFrame, now ).TotalSeconds;
				lastFrame = now;
				video?.Present();
				if ( video is not null && Audio.Mixer.Master is not null )
				{
					Audio.MixingThread.UpdateGlobals();
					Audio.AudioEngine.Tick();
				}
				if ( ready ) imageElapsed += Math.Min( frameTime, 0.1f );
				var elapsed = imageElapsed;
				if ( brandingTexture is { IsLoaded: true } && !ready )
				{
					ready = true;
					started = Stopwatch.GetTimestamp();
					elapsed = 0;
				}
				if ( video is not null )
				{
					if ( video.PlaybackTime > lastPlayback )
					{
						lastPlayback = video.PlaybackTime;
						lastProgress = Stopwatch.GetTimestamp();
					}
					if ( Stopwatch.GetElapsedTime( lastProgress ).TotalSeconds > 15 )
						throw new TimeoutException( "Startup video stopped producing frames" );
					elapsed = video.PlaybackTime;
				}
				var duration = video?.Duration ?? entry.Duration + entry.FadeIn + entry.FadeOut;
				if ( ready && ((finished && (duration <= 0 || elapsed >= duration - 0.1f)) || ((video is null || duration > 0) && elapsed >= duration)) ) break;
				if ( !ready && Stopwatch.GetElapsedTime( started ).TotalSeconds > 15 )
					throw new TimeoutException( "Startup branding did not load" );
				brandingOpacity = ready ? entry.Opacity( elapsed, video is not null && duration <= 0 ? float.PositiveInfinity : duration ) : 0;
				DrawStartupImage( startupProgress );
				await Task.Delay( 16 );
			}
		}
		finally
		{
			brandingTexture = null;
		}
	}
}
