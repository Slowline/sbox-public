using Editor;
using System;
using System.Text.Json;

namespace StandaloneTests;

[TestClass]
public class ExportConfigTest
{
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
