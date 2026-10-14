using Editor;
using System;
using System.IO;

namespace StandaloneTests;

[TestClass]
public class StartupVideoPreviewTests
{
	[TestMethod]
	public async Task PreviewUsesTheFirstFrame()
	{
		if ( !Graphics.IsAvailable ) Assert.Inconclusive( "Requires graphics for the video texture readback." );
		var source = Path.Combine( AppContext.BaseDirectory, "Standalone", "TestData", "preview.webm" );
		using var bitmap = Bitmap.CreateFromBytes( await StandaloneExporter.CreateStartupVideoPreview( source ) );
		Assert.AreEqual( 16, bitmap.Width );
		Assert.AreEqual( 16, bitmap.Height );
		var pixel = bitmap.GetPixel( 8, 8 );
		Assert.IsTrue( pixel.r > 0.9f && pixel.g < 0.1f && pixel.b < 0.1f, $"The first frame is red; the second is blue. Actual: {pixel}" );
	}

	[TestMethod]
	public async Task MissingVideoFailsBeforeDecoding()
	{
		await Assert.ThrowsExceptionAsync<FileNotFoundException>( () =>
			StandaloneExporter.CreateStartupVideoPreview( Path.Combine( Path.GetTempPath(), $"{Guid.NewGuid():N}.webm" ) ) );
	}

	[TestMethod]
	public async Task InvalidVideoCannotHangTheExport()
	{
		var source = Path.Combine( Path.GetTempPath(), $"{Guid.NewGuid():N}.webm" );
		try
		{
			File.WriteAllText( source, "not a video" );
			await Assert.ThrowsExceptionAsync<TimeoutException>( () => StandaloneExporter.CreateStartupVideoPreview( source ) );
		}
		finally { File.Delete( source ); }
	}
}
