using System.IO;

namespace Sandbox;

/// <summary>
/// Reads a VPK directory without extracting files or requiring its external data chunks.
/// Shared by map analysis and the standalone archive filesystem.
/// </summary>
internal static class VpkDirectory
{
	internal const uint Signature = 0x55AA1234;
	internal record Entry( string Archive, long Offset, long Length, byte[] Preload, bool IsLoose = false );

	/// <summary>
	/// Every file in the VPK with its size in bytes, relative to the archive root.
	/// </summary>
	public static List<(string Path, long Size)> Read( string filename ) => ReadEntries( filename )
		.Select( x => (x.Key[1..], x.Value.Length + x.Value.Preload.Length) ).ToList();

	internal static Dictionary<string, Entry> ReadEntries( string filename )
	{
		filename = Path.GetFullPath( filename );
		using var stream = File.OpenRead( filename );
		using var reader = new BinaryReader( stream );
		if ( reader.ReadUInt32() != Signature ) throw new InvalidDataException( $"{filename} isn't a VPK" );
		var version = reader.ReadUInt32();
		var treeSize = reader.ReadUInt32();
		var embeddedSize = version == 2 ? reader.ReadUInt32() : 0;
		if ( version == 2 ) reader.ReadBytes( 12 );
		else if ( version != 1 ) throw new InvalidDataException( $"Unsupported VPK version {version}" );
		var treeEnd = stream.Position + treeSize;
		if ( treeEnd > stream.Length ) throw new InvalidDataException( "Truncated VPK directory" );
		var entries = new Dictionary<string, Entry>( StringComparer.OrdinalIgnoreCase );

		string ReadString()
		{
			var bytes = new List<byte>();
			while ( stream.Position < treeEnd )
			{
				var value = reader.ReadByte();
				if ( value == 0 ) return Encoding.UTF8.GetString( bytes.ToArray() );
				bytes.Add( value );
			}
			throw new InvalidDataException( "Unterminated VPK directory string" );
		}

		for ( var extension = ReadString(); extension.Length > 0; extension = ReadString() )
		{
			for ( var directory = ReadString(); directory.Length > 0; directory = ReadString() )
			{
				for ( var name = ReadString(); name.Length > 0; name = ReadString() )
				{
					if ( stream.Position + 18 > treeEnd ) throw new InvalidDataException( "Truncated VPK entry" );
					reader.ReadUInt32(); // CRC
					var preloadLength = reader.ReadUInt16();
					var archiveIndex = reader.ReadUInt16();
					var offset = reader.ReadUInt32();
					var length = reader.ReadUInt32();
					if ( reader.ReadUInt16() != 0xFFFF ) throw new InvalidDataException( "Invalid VPK entry" );
					var preload = reader.ReadBytes( preloadLength );
					if ( preload.Length != preloadLength || stream.Position > treeEnd ) throw new InvalidDataException( "Truncated VPK entry" );
					var path = (directory == " " ? "" : directory + "/") + name + (extension == " " ? "" : "." + extension);
					if ( path.Contains( '\\' ) || path.Split( '/' ).Any( x => x is "" or "." or ".." ) || path.Contains( ':' ) )
						throw new InvalidDataException( "Invalid VPK path" );
					var archive = filename;
					long dataOffset = treeEnd + offset;
					if ( archiveIndex != 0x7FFF )
					{
						archive = ArchiveBase( filename ) + $"_{archiveIndex:D3}.vpk";
						dataOffset = offset;
					}
					else if ( dataOffset + length > (version == 2 ? treeEnd + embeddedSize : stream.Length) )
						throw new InvalidDataException( $"Invalid embedded VPK data range for {path}" );
					if ( !entries.TryAdd( "/" + path, new( archive, dataOffset, length, preload ) ) ) throw new InvalidDataException( $"Duplicate VPK path {path}" );
				}
			}
		}
		if ( stream.Position != treeEnd ) throw new InvalidDataException( "Invalid VPK directory size" );
		return entries;
	}

	internal static string ArchiveBase( string filename ) => filename.EndsWith( "_dir.vpk", StringComparison.OrdinalIgnoreCase ) ? filename[..^8] : filename[..^4];
}
