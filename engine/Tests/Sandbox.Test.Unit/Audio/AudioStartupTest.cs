using Sandbox.Audio;

namespace AudioTests;

[TestClass]
public class AudioStartupTest
{
	[TestMethod]
	public void TickBeforeMixerInitializationDoesNotTouchNativeDevice()
	{
		Assert.IsNull( Mixer.Master, "Unit tests must not initialize the native mixing graph" );
		AudioEngine.Tick();
		Assert.IsNull( Mixer.Master );
	}
}
