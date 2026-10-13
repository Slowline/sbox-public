using System.IO;
using System.Security.Cryptography;
using Sandbox.Utility;

namespace Sandbox;

/// <summary>
/// The VPK directory and chunk files used by standalone exports.
/// </summary>
internal sealed class VpkArchive
{
	internal const uint Signature = 0x55AA1234;
	internal record Entry( string Archive, long Offset, long Length, byte[] Preload, bool IsLoose = false );
	internal Dictionary<string, Entry> Entries { get; } = new( StringComparer.OrdinalIgnoreCase );

	internal VpkArchive( string filename )
	{
		filename = Path.GetFullPath( filename );
		using var stream = File.OpenRead( filename );
		using var reader = new BinaryReader( stream );
		if ( reader.ReadUInt32() != Signature ) throw new InvalidDataException( "Invalid VPK signature" );
		var version = reader.ReadUInt32();
		var treeSize = reader.ReadUInt32();
		var embeddedSize = version == 2 ? reader.ReadUInt32() : 0;
		if ( version == 2 ) reader.ReadBytes( 12 );
		else if ( version != 1 ) throw new InvalidDataException( $"Unsupported VPK version {version}" );
		var treeEnd = stream.Position + treeSize;
		if ( treeEnd > stream.Length ) throw new InvalidDataException( "Truncated VPK directory" );

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
					var dataEnd = archive == filename && version == 2 ? treeEnd + embeddedSize : new FileInfo( archive ).Length;
					if ( dataOffset + length > dataEnd ) throw new InvalidDataException( $"Truncated VPK data for {path}" );
					if ( !Entries.TryAdd( "/" + path, new( archive, dataOffset, length, preload ) ) ) throw new InvalidDataException( $"Duplicate VPK path {path}" );
				}
			}
		}
		if ( stream.Position != treeEnd ) throw new InvalidDataException( "Invalid VPK directory size" );
	}

	internal Stream OpenRead( string path )
	{
		if ( !Entries.TryGetValue( path, out var entry ) ) throw new FileNotFoundException( path );
		return new EntryStream( entry );
	}

	/// <summary>
	/// Write a standard VPK v2 directory with separate data chunks, keeping memory bounded.
	/// </summary>
	internal static async Task WriteAsync( string folder, string filename, long chunkSize = 1024L * 1024 * 1024 )
	{
		if ( !filename.EndsWith( ".vpk", StringComparison.OrdinalIgnoreCase ) ) throw new ArgumentException( "VPK filename must end in .vpk", nameof( filename ) );
		var archiveBase = ArchiveBase( filename );
		if ( chunkSize <= 0 || chunkSize > uint.MaxValue ) throw new ArgumentOutOfRangeException( nameof( chunkSize ) );
		var files = Directory.EnumerateFiles( folder, "*", SearchOption.AllDirectories )
			.Select( source => (Source: source, Path: Path.GetRelativePath( folder, source ).Replace( '\\', '/' ).ToLowerInvariant()) )
			.OrderBy( x => x.Path, StringComparer.Ordinal ).ToArray();
		if ( files.Select( x => x.Path ).Distinct( StringComparer.Ordinal ).Count() != files.Length ) throw new InvalidDataException( "VPK paths differ only by case" );
		using var tree = new MemoryStream();
		using var writer = new BinaryWriter( tree, Encoding.UTF8, true );
		void WriteString( string value ) { writer.Write( Encoding.UTF8.GetBytes( value ) ); writer.Write( (byte)0 ); }
		FileStream chunk = null;
		ushort index = 0;
		try
		{
			foreach ( var extensions in files.GroupBy( x => Path.GetExtension( x.Path ) ) )
			{
				WriteString( extensions.Key.Length == 0 ? " " : extensions.Key[1..] );
				foreach ( var directories in extensions.GroupBy( x => Path.GetDirectoryName( x.Path )?.Replace( '\\', '/' ) ) )
				{
					WriteString( string.IsNullOrEmpty( directories.Key ) ? " " : directories.Key );
					foreach ( var file in directories )
					{
#pragma warning disable CA2000 // The using declaration disposes the input; analysis loses track across the chunk rollover.
						using var input = File.OpenRead( file.Source );
#pragma warning restore CA2000
						if ( input.Length > uint.MaxValue ) throw new InvalidDataException( $"VPK file exceeds 4 GiB: {file.Path}" );
						if ( chunk is not null && chunk.Position + input.Length > chunkSize )
						{
							chunk.Dispose();
							chunk = null;
							if ( ++index == 0x7FFF ) throw new InvalidDataException( "Too many VPK chunks" );
						}
						chunk ??= File.Create( archiveBase + $"_{index:D3}.vpk" );
						var crc = await Crc32.FromStreamAsync( input );
						input.Position = 0;
						WriteString( Path.GetFileNameWithoutExtension( file.Path ) );
						writer.Write( crc );
						writer.Write( (ushort)0 );
						writer.Write( index == 0 ? (ushort)0x7FFF : index );
						writer.Write( checked((uint)chunk.Position) );
						writer.Write( checked((uint)input.Length) );
						writer.Write( (ushort)0xFFFF );
						await input.CopyToAsync( chunk );
					}
					writer.Write( (byte)0 );
				}
				writer.Write( (byte)0 );
			}
			writer.Write( (byte)0 );
		}
		finally { chunk?.Dispose(); }
		using var chunkHashes = new MemoryStream();
		using var hashWriter = new BinaryWriter( chunkHashes, Encoding.UTF8, true );
#pragma warning disable CA5351 // VPK v2 specifies MD5 checksums; these are not used for security.
		for ( var chunkIndex = 1; chunkIndex <= index; chunkIndex++ )
		{
			using var data = File.OpenRead( archiveBase + $"_{chunkIndex:D3}.vpk" );
			hashWriter.Write( (uint)chunkIndex );
			hashWriter.Write( 0u );
			hashWriter.Write( checked((uint)data.Length) );
			hashWriter.Write( MD5.HashData( data ) );
		}
#pragma warning restore CA5351
		using var output = File.Create( filename );
		using var header = new BinaryWriter( output, Encoding.UTF8, true );
		var embedded = archiveBase + "_000.vpk";
		var embeddedSize = File.Exists( embedded ) ? checked((uint)new FileInfo( embedded ).Length) : 0;
		header.Write( Signature );
		header.Write( 2u );
		header.Write( checked((uint)tree.Length) );
		header.Write( embeddedSize );
		header.Write( checked((uint)chunkHashes.Length) );
		header.Write( 48u ); // Directory, chunk-section and whole-file MD5s
		header.Write( 0u ); // No signature
		tree.Position = 0;
		await tree.CopyToAsync( output );
		if ( File.Exists( embedded ) )
		{
			using ( var data = File.OpenRead( embedded ) ) await data.CopyToAsync( output );
			File.Delete( embedded );
		}
		chunkHashes.Position = 0;
		await chunkHashes.CopyToAsync( output );
#pragma warning disable CA5351 // VPK v2 specifies MD5 checksums; these are not used for security.
		output.Write( MD5.HashData( tree.ToArray() ) );
		output.Write( MD5.HashData( chunkHashes.ToArray() ) );
		output.Position = 0;
		var checksum = MD5.HashData( output );
#pragma warning restore CA5351
		output.Position = output.Length;
		output.Write( checksum );
	}

	static string ArchiveBase( string filename ) => filename.EndsWith( "_dir.vpk", StringComparison.OrdinalIgnoreCase ) ? filename[..^8] : filename[..^4];

	internal static bool IsArchiveFile( string archive, string file )
	{
		archive = Path.GetFullPath( archive );
		file = Path.GetFullPath( file );
		if ( string.Equals( archive, file, StringComparison.OrdinalIgnoreCase ) ) return true;
		if ( !string.Equals( Path.GetDirectoryName( archive ), Path.GetDirectoryName( file ), StringComparison.OrdinalIgnoreCase ) ) return false;
		var prefix = Path.GetFileName( ArchiveBase( archive ) ) + "_";
		var name = Path.GetFileNameWithoutExtension( file );
		return name.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) && ushort.TryParse( name[prefix.Length..], out _ );
	}

	sealed class EntryStream : Stream
	{
		readonly Entry entry;
		readonly FileStream file;
		long position;
		internal EntryStream( Entry entry ) { this.entry = entry; file = File.OpenRead( entry.Archive ); }
		public override bool CanRead => true;
		public override bool CanSeek => true;
		public override bool CanWrite => false;
		public override long Length => entry.Preload.Length + (long)entry.Length;
		public override long Position { get => position; set => Seek( value, SeekOrigin.Begin ); }
		public override int Read( byte[] buffer, int offset, int count ) => Read( buffer.AsSpan( offset, count ) );
		public override int Read( Span<byte> buffer )
		{
			var count = (int)Math.Min( buffer.Length, Math.Max( 0, Length - position ) );
			var preload = (int)Math.Min( count, Math.Max( 0, entry.Preload.Length - position ) );
			if ( preload > 0 ) entry.Preload.AsSpan( (int)position, preload ).CopyTo( buffer );
			position += preload;
			var read = 0;
			if ( count > preload )
			{
				file.Position = entry.Offset + position - entry.Preload.Length;
				read = file.Read( buffer.Slice( preload, count - preload ) );
				if ( read == 0 ) throw new EndOfStreamException( "Truncated VPK data" );
				position += read;
			}
			return preload + read;
		}
		public override long Seek( long offset, SeekOrigin origin )
		{
			var target = checked(offset + (origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => position, SeekOrigin.End => Length, _ => throw new ArgumentOutOfRangeException( nameof( origin ) ) }));
			if ( target < 0 ) throw new IOException( "Cannot seek before a VPK entry" );
			return position = target;
		}
		protected override void Dispose( bool disposing ) { if ( disposing ) file.Dispose(); base.Dispose( disposing ); }
		public override void Flush() { }
		public override void SetLength( long value ) => throw new NotSupportedException();
		public override void Write( byte[] buffer, int offset, int count ) => throw new NotSupportedException();
	}
}
