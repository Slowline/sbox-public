using System;
using System.IO;

namespace StandaloneTests;

[TestClass]
public class VpkMountTest
{
	[TestMethod]
	public async Task NativeResourcesLoadFromExportArchive()
	{
		var root = Path.Combine( Path.GetTempPath(), $"sbox-vpk-mount-{Guid.NewGuid():N}" );
		var source = Path.Combine( root, "source" );
		Directory.CreateDirectory( source );
		var resource = $"vpk_test_{Guid.NewGuid():N}";
		var archive = Path.Combine( root, Standalone.AssetsArchiveName );
		try
		{
			var original = Path.GetFullPath( "core/materials/shine.vmat_c" );
			var expected = NativeEngine.EngineGlue.ReadCompiledResourceFileJsonFromFilesystem( original );
			Assert.IsFalse( string.IsNullOrWhiteSpace( expected ), "Fixture must be readable by the native filesystem" );
			File.Copy( original, Path.Combine( source, resource + ".vmat_c" ) );
			File.Copy( original, Path.Combine( source, resource + "_external.vmat_c" ) );
			File.Copy( "core/textures/dev/white.vtex_c", Path.Combine( source, resource + ".vtex_c" ) );
			Directory.CreateDirectory( Path.Combine( root, "maps" ) );
			File.Copy( "core/maps/error.vpk", Path.Combine( root, "maps", resource + ".vpk" ) );
			await VpkArchive.WriteAsync( source, archive, 8 );
			NativeEngine.EngineGlue.AddSearchPath( archive, "GAME", true );
			NativeEngine.EngineGlue.AddSearchPath( root, "GAME", false );
			try
			{
				Assert.AreEqual( expected, NativeEngine.EngineGlue.ReadCompiledResourceFileJsonFromFilesystem( resource + ".vmat_c" ), "Native filesystem must find the compiled resource inside the VPK" );
				Assert.AreEqual( expected, NativeEngine.EngineGlue.ReadCompiledResourceFileJsonFromFilesystem( resource + "_external.vmat_c" ), "Native filesystem must read external VPK chunks" );
				var texture = Texture.FromNative( NativeGlue.Resources.GetTexture( resource + ".vtex", Guid.Empty ) );
				Assert.IsTrue( texture is { IsValid: true, IsError: false }, "Native resource loader must load textures from the archive" );
				var packed = new VpkFileSystem( archive );
				var map = $"maps/{resource}.vpk";
				try
				{
					Assert.AreEqual( Path.Combine( root, "maps", resource + ".vpk" ), packed.GetFullPath( map ), "Maps must use their original loose file" );
					Assert.IsTrue( g_pWorldRendererMgr.MountWorldVPK( resource, map ), "Loose map VPKs beside the assets archive must mount" );
					g_pWorldRendererMgr.UnmountWorldVPK( resource );
				}
				finally
				{
					packed.Dispose();
				}
			}
			finally
			{
				NativeEngine.EngineGlue.RemoveSearchPath( root, "GAME" );
				NativeEngine.EngineGlue.RemoveSearchPath( archive, "GAME" );
			}
		}
		finally { Directory.Delete( root, true ); }
	}
}
