using Editor;
using Sandbox;
using System;
using System.Text.Json;

namespace StandaloneTests;

[TestClass]
public class ExportConfigTest
{
	[TestMethod]
	public void BrandingEntriesHaveAnEditorSerializedObject()
	{
		var library = new Sandbox.Internal.TypeLibrary();
		library.AddAssembly( typeof( Vector3 ).Assembly, false );
		library.AddAssembly( typeof( StartupBranding ).Assembly, false );
		library.AddAssembly( typeof( ExportConfig ).Assembly, true );
		Assert.IsNotNull( library.GetType( typeof( StartupBranding ) ), "The editor must register the branding entry type" );
		var config = new ExportConfig { StartupBranding = [] };
		var property = library.GetSerializedObject( config ).GetProperty( nameof( ExportConfig.StartupBranding ) );
		Assert.IsTrue( property.TryGetAsObject( out var list ) );
		var collection = (SerializedCollection)list;
		Assert.AreEqual( 0, collection.Count() );
		collection.Add( null );
		// ControlObjectWidget creates the default instance before requesting its editor object.
		collection.First().SetValue( new StartupBranding() );
		Assert.IsTrue( collection.First().TryGetAsObject( out var entry ), "New branding entries must open in the property editor" );
		Assert.IsNotNull( entry.GetProperty( nameof( StartupBranding.Path ) ) );
		entry.GetProperty( nameof( StartupBranding.FadeIn ) ).SetValue( 2f );
		Assert.AreEqual( 2f, config.StartupBranding[0].FadeIn );
	}

	[TestMethod]
	public void BrandingPresetsKeepOrderAndTimings()
	{
		var config = new ExportConfig
		{
			StartupBranding =
			[
				new() { Path = "intro.webm", Duration = 7, FadeIn = 0, FadeOut = 1 },
				new() { Path = "logo.png", Duration = 4, FadeIn = 2, FadeOut = 0 }
			]
		};
		var loaded = JsonSerializer.Deserialize<ExportConfig>( JsonSerializer.Serialize( config ) );
		Assert.AreEqual( "intro.webm", loaded.StartupBranding[0].Path );
		Assert.AreEqual( 1f, loaded.StartupBranding[0].FadeOut );
		Assert.AreEqual( "logo.png", loaded.StartupBranding[1].Path );
		Assert.AreEqual( 4f, loaded.StartupBranding[1].Duration );
		Assert.AreEqual( 2f, loaded.StartupBranding[1].FadeIn );
		Assert.AreEqual( 0f, loaded.StartupBranding[1].FadeOut );
		Assert.IsNull( JsonSerializer.Deserialize<ExportConfig>( "{\"StartupImage\":\"old.vtex\"}" ).StartupBranding );
		Assert.AreEqual( 0, JsonSerializer.Deserialize<ExportConfig>( "{\"StartupBranding\":[]}" ).StartupBranding.Count );
	}

	[TestMethod]
	public void BrandingFadesAndCuts()
	{
		var entry = new StartupBranding { FadeIn = 1, FadeOut = 2 };
		Assert.AreEqual( 0f, entry.Opacity( 0, 6 ) );
		Assert.AreEqual( 0.5f, entry.Opacity( 0.5f, 6 ) );
		Assert.AreEqual( 1f, entry.Opacity( 2, 6 ) );
		Assert.AreEqual( 0.5f, entry.Opacity( 5, 6 ) );
		Assert.AreEqual( 0f, entry.Opacity( 7, 6 ) );
		entry.FadeIn = entry.FadeOut = 0;
		Assert.AreEqual( 1f, entry.Opacity( 0, 6 ) );
	}

	[TestMethod]
	public void BrandingRejectsInvalidMediaAndTimings()
	{
		foreach ( var path in new[] { null, "", "logo.exe" } )
			Assert.ThrowsException<ArgumentException>( () => new StartupBranding { Path = path }.Validate() );
		foreach ( var value in new[] { -1f, float.NaN, float.PositiveInfinity } )
		{
			Assert.ThrowsException<ArgumentException>( () => new StartupBranding { Path = "logo.png", Duration = value }.Validate() );
			Assert.ThrowsException<ArgumentException>( () => new StartupBranding { Path = "logo.png", FadeIn = value }.Validate() );
			Assert.ThrowsException<ArgumentException>( () => new StartupBranding { Path = "logo.png", FadeOut = value }.Validate() );
		}
		new StartupBranding { Path = "INTRO.MP4" }.Validate();
		Assert.IsTrue( new StartupBranding { Path = "INTRO.MP4" }.IsVideo );
		Assert.ThrowsException<ArgumentException>( () => new StartupBranding { Path = "logo.png", Duration = float.MaxValue, FadeIn = float.MaxValue }.Validate() );
	}

	[TestMethod]
	public void PresetsKeepExecutableVersion()
	{
		var saved = JsonSerializer.Serialize( new ExportConfig { ExecutableVersion = "2.3.4.5" } );
		Assert.AreEqual( "2.3.4.5", JsonSerializer.Deserialize<ExportConfig>( saved ).ExecutableVersion );
		Assert.AreEqual( "1.0.0.0", JsonSerializer.Deserialize<ExportConfig>( "{}" ).ExecutableVersion );
	}

	[TestMethod]
	[DataRow( "1.0.0.0" )]
	[DataRow( "2.3.4.5" )]
	[DataRow( "65535.65535.65535.65535" )]
	public void AcceptsExecutableVersions( string value )
	{
		Assert.AreEqual( value, StandaloneExporter.ParseExecutableVersion( value ).ToString( 4 ) );
	}

	[TestMethod]
	[DataRow( "" )]
	[DataRow( "1.2.3" )]
	[DataRow( "1.2.3.4.5" )]
	[DataRow( "1.0.0.-1" )]
	[DataRow( "65536.0.0.0" )]
	[DataRow( "0.65536.0.0" )]
	[DataRow( "0.0.65536.0" )]
	[DataRow( "0.0.0.65536" )]
	public void RejectsInvalidExecutableVersions( string value )
	{
		Assert.ThrowsException<ArgumentException>( () => StandaloneExporter.ParseExecutableVersion( value ) );
	}
}
