using System.IO;
using Zio;
using Zio.FileSystems;

namespace Sandbox;

/// <summary>
/// Read game files directly from a VPK without unpacking the archive into memory or onto disk.
/// </summary>
internal sealed class VpkFileSystem : BaseFileSystem
{
	readonly ArchiveFileSystem archiveSystem;
	internal VpkFileSystem( string filename )
	{
		filename = Path.GetFullPath( filename );
		var archive = new VpkArchive( filename );
		var folder = Path.GetDirectoryName( filename );
		foreach ( var file in Directory.EnumerateFiles( folder, "*", SearchOption.AllDirectories ).Where( x => x.EndsWith( ".vpk", StringComparison.OrdinalIgnoreCase ) ) )
		{
			if ( VpkArchive.IsArchiveFile( filename, file ) ) continue;
			var path = "/" + Path.GetRelativePath( folder, file ).Replace( '\\', '/' );
			archive.Entries.TryAdd( path, new( file, 0, new FileInfo( file ).Length, [], true ) );
		}
		archiveSystem = new ArchiveFileSystem( archive );
		system = new ReadOnlyFileSystem( archiveSystem );
		SuppressZioFinalizer( system );
		WatchEnabled = false;
	}
	internal override void Dispose()
	{
		base.Dispose();
		archiveSystem.Dispose();
	}

	sealed class ArchiveFileSystem : PhysicalFileSystem
	{
		readonly VpkArchive archive;
		readonly HashSet<UPath> directories = new();
		readonly Dictionary<string, UPath> paths = new( StringComparer.OrdinalIgnoreCase );
		readonly string cache = Path.Combine( Path.GetTempPath(), $"sbox-vpk-cache-{Guid.NewGuid():N}" );
		internal ArchiveFileSystem( VpkArchive archive )
		{
			this.archive = archive;
			foreach ( var filename in archive.Entries.Keys )
			{
				paths[filename] = new UPath( filename );
				for ( var path = new UPath( filename ).GetDirectory(); !path.IsEmpty; path = path.GetDirectory() )
				{
					directories.Add( path );
					if ( path == UPath.Root ) break;
				}
			}
			directories.Add( UPath.Root );
			foreach ( var directory in directories ) paths[directory.FullName] = directory;
			SuppressZioFinalizer( this );
		}
		// Video decoders and callers requesting a physical filename need just that file on disk.
		protected override string ConvertPathToInternalImpl( UPath path )
		{
			if ( paths.TryGetValue( path.FullName, out var canonical ) ) path = canonical;
			if ( archive.Entries.TryGetValue( path.FullName, out var entry ) && entry.IsLoose ) return entry.Archive;
			var target = Path.Combine( cache, path.FullName.TrimStart( '/' ) );
			if ( !FileExistsImpl( path ) ) return target;
			lock ( archive )
			{
				if ( File.Exists( target ) ) return target;
				Directory.CreateDirectory( Path.GetDirectoryName( target ) );
				try
				{
					using var input = archive.OpenRead( path.FullName );
					using var output = File.Create( target );
					input.CopyTo( output );
				}
				catch { File.Delete( target ); throw; }
			}
			return target;
		}
		protected override UPath ConvertPathFromInternalImpl( string path )
		{
			foreach ( var (name, entry) in archive.Entries )
				if ( entry.IsLoose && string.Equals( entry.Archive, path, StringComparison.OrdinalIgnoreCase ) ) return new UPath( name );
			var relative = Path.GetRelativePath( cache, path ).Replace( '\\', '/' );
			if ( relative == ".." || relative.StartsWith( "../" ) || Path.IsPathRooted( relative ) ) throw new ArgumentException( "Path is outside VPK cache", nameof( path ) );
			return relative == "." ? UPath.Root : new UPath( "/" + relative );
		}
		protected override void Dispose( bool disposing )
		{
			if ( disposing && Directory.Exists( cache ) ) Directory.Delete( cache, true );
			base.Dispose( disposing );
		}
		protected override bool FileExistsImpl( UPath path ) => archive.Entries.ContainsKey( path.FullName );
		protected override bool DirectoryExistsImpl( UPath path ) => paths.TryGetValue( path.FullName, out var canonical ) && directories.Contains( canonical );
		protected override long GetFileLengthImpl( UPath path ) => archive.Entries.TryGetValue( path.FullName, out var entry ) ? entry.Length + (long)entry.Preload.Length : throw new FileNotFoundException( path.FullName );
		protected override Stream OpenFileImpl( UPath path, FileMode mode, FileAccess access, FileShare share )
		{
			if ( mode != FileMode.Open || access != FileAccess.Read ) throw new NotSupportedException( "VPK files are read only" );
			return archive.OpenRead( path.FullName );
		}
		protected override IEnumerable<UPath> EnumeratePathsImpl( UPath path, string searchPattern, SearchOption searchOption, SearchTarget searchTarget )
		{
			if ( paths.TryGetValue( path.FullName, out var canonical ) ) path = canonical;
			var pattern = SearchPattern.Parse( ref path, ref searchPattern );
			var results = Enumerable.Empty<UPath>();
			if ( searchTarget != SearchTarget.Directory ) results = results.Concat( archive.Entries.Keys.Select( x => new UPath( x ) ) );
			if ( searchTarget != SearchTarget.File ) results = results.Concat( directories.Where( x => x != UPath.Root ) );
			return results.Where( x => x.IsInDirectory( path, searchOption == SearchOption.AllDirectories ) && pattern.Match( x ) );
		}
		protected override IEnumerable<FileSystemItem> EnumerateItemsImpl( UPath path, SearchOption searchOption, SearchPredicate searchPredicate )
		{
			foreach ( var entry in EnumeratePathsImpl( path, "*", searchOption, SearchTarget.Both ) )
			{
				var item = new FileSystemItem( this, entry, DirectoryExistsImpl( entry ) );
				if ( searchPredicate is null || searchPredicate( ref item ) ) yield return item;
			}
		}
		protected override FileAttributes GetAttributesImpl( UPath path ) => DirectoryExistsImpl( path ) ? FileAttributes.Directory | FileAttributes.ReadOnly : FileExistsImpl( path ) ? FileAttributes.ReadOnly : throw new FileNotFoundException( path.FullName );
		protected override DateTime GetCreationTimeImpl( UPath path ) => DateTime.UnixEpoch;
		protected override DateTime GetLastWriteTimeImpl( UPath path ) => DateTime.UnixEpoch;
		protected override DateTime GetLastAccessTimeImpl( UPath path ) => DateTime.UnixEpoch;
		protected override bool CanWatchImpl( UPath path ) => false;
	}
}
