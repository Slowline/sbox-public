using System;
using System.IO;
using System.Text;

namespace FilesystemTests;

[TestClass]
public class VpkTest
{
	[TestMethod]
	public async Task RoundTripChunkedAssets()
	{
		var root = Path.Combine( Path.GetTempPath(), $"sbox-vpk-test-{Guid.NewGuid():N}" );
		var source = Path.Combine( root, "source" );
		Directory.CreateDirectory( Path.Combine( source, "Localization", "en" ) );
		Directory.CreateDirectory( Path.Combine( source, ".bin" ) );
		try
		{
			File.WriteAllText( Path.Combine( source, "Localization", "en", "hello.txt" ), "hello world" );
			File.WriteAllBytes( Path.Combine( source, ".bin", "game.dll" ), [0, 1, 2, 255] );
			File.WriteAllText( Path.Combine( source, "no_extension" ), "root" );
			File.WriteAllBytes( Path.Combine( source, "empty.dat" ), [] );
			var filename = Path.Combine( root, "assets.vpk" );
			await VpkArchive.WriteAsync( source, filename, 8 );
			CollectionAssert.AreEquivalent( new[] { (".bin/game.dll", 4L), ("empty.dat", 0L), ("localization/en/hello.txt", 11L), ("no_extension", 4L) }, VpkDirectory.Read( filename ).ToArray() );
			Directory.CreateDirectory( Path.Combine( root, "maps" ) );
			var map = Path.Combine( root, "maps", "level.vpk" );
			File.WriteAllBytes( map, [11, 12] );
			Assert.IsTrue( Directory.GetFiles( root, "assets_*.vpk" ).Length >= 2, "Exercise multiple chunks" );
			var fs = new VpkFileSystem( filename );
			try
			{
				Assert.IsTrue( fs.IsReadOnly );
				using var watcher = fs.Watch();
				Assert.IsTrue( fs.DirectoryExists( "LOCALIZATION/EN" ) );
				Assert.AreEqual( "hello world", fs.ReadAllText( "LOCALIZATION/EN/HELLO.TXT" ) );
				Assert.AreEqual( "root", fs.ReadAllText( "no_extension" ) );
				CollectionAssert.AreEqual( new byte[] { 0, 1, 2, 255 }, fs.ReadAllBytes( ".bin/game.dll" ).ToArray() );
				Assert.AreEqual( 0, fs.ReadAllBytes( "empty.dat" ).Length );
				Assert.AreEqual( 5, fs.FindFile( "/", recursive: true ).Count() );
				Assert.AreEqual( map, fs.GetFullPath( "maps/level.vpk" ) );
				CollectionAssert.AreEqual( new byte[] { 11, 12 }, fs.ReadAllBytes( "maps/level.vpk" ).ToArray() );
				CollectionAssert.AreEqual( new[] { "maps/level.vpk" }, fs.FindFile( "/", "*.vpk", true ).ToArray() );
				var localization = fs.CreateSubSystem( "Localization" );
				try
				{
					Assert.AreEqual( "hello world", localization.ReadAllText( "en/hello.txt" ) );
					CollectionAssert.AreEqual( new[] { "en/hello.txt" }, localization.FindFile( "/", recursive: true ).ToArray() );
				}
				finally { localization.Dispose(); }
				var mounted = new AggregateFileSystem();
				try
				{
					mounted.Mount( fs );
					Assert.AreEqual( "root", mounted.ReadAllText( "no_extension" ) );
					CollectionAssert.AreEquivalent( new[] { "localization", ".bin", "maps" }, mounted.FindDirectory( "/" ).ToArray() );
				}
				finally { mounted.Dispose(); }
				using var stream = fs.OpenRead( "Localization/en/hello.txt" );
				stream.Seek( -5, SeekOrigin.End );
				var buffer = new byte[20];
				Assert.AreEqual( 5, stream.Read( buffer ) );
				Assert.AreEqual( "world", Encoding.UTF8.GetString( buffer, 0, 5 ) );
				Assert.AreEqual( 0, stream.Read( buffer ) );
				Assert.AreEqual( "root", File.ReadAllText( fs.GetFullPath( "no_extension" ) ) );
				Assert.ThrowsException<FileNotFoundException>( () => fs.OpenRead( "missing.txt" ) );
			}
			finally { fs.Dispose(); }
			using ( var chunk = File.OpenWrite( Path.Combine( root, "assets_001.vpk" ) ) ) chunk.SetLength( 0 );
			Assert.ThrowsException<InvalidDataException>( () => new VpkArchive( filename ) );
			Assert.AreEqual( 4, VpkDirectory.Read( filename ).Count, "Map analysis must only need the directory, even when a data chunk is incomplete" );
		}
		finally { Directory.Delete( root, true ); }
	}
}
