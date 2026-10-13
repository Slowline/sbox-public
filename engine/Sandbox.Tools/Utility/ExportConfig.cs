using System;

namespace Editor;

public class ExportConfig
{
	public Project Project { get; set; }

	/// <summary>
	/// Assemblies can reference asset packages. This is a list
	/// of packages that the compiled code references.
	/// </summary>
	public HashSet<string> CodePackages = new();

	/// <summary>
	/// If the compile process created any assemblies
	/// </summary>
	public Dictionary<string, object> AssemblyFiles { get; set; }

	/// <summary>
	/// Where are we putting the exported build?
	/// </summary>
	[Title( "Export Directory" ), Editor( "folder" )]
	public string TargetDir { get; set; }

	/// <summary>
	/// The target .exe name for this export
	/// </summary>
	[Title( "Executable Name" )]
	public string ExecutableName { get; set; }

	/// <summary>
	/// File and product version shown in the executable's Windows properties.
	/// </summary>
	[Title( "Executable Version" ), Description( "Four numbers from 0 to 65535, e.g. 1.0.0.0" )]
	public string ExecutableVersion { get; set; } = "1.0.0.0";

	/// <summary>
	/// The icon for the target .exe
	/// </summary>
	[Title( "Executable Icon" )] // can't make this a .ico picker yet, we should really just convert from png ourselves though
	public string TargetIcon { get; set; }

	/// <summary>
	/// The splash screen to use
	/// </summary>
	[Title( "Startup Image" ), ResourceType( "vtex" )] // should make this png
	public string StartupImage { get; set; }

	/// <summary>
	/// Pack game content into a VPK instead of exporting loose files.
	/// </summary>
	[Title( "Pack Assets into VPK" )]
	public bool PackAssets { get; set; }

	/// <summary>
	/// Omit files without discovered references, keeping explicit Resources and uncertain dynamic loads.
	/// </summary>
	[Title( "Exclude Unused Files" ), Description( "Analyze code and asset references before exporting. Explicit Resources and uncertain dynamic loads are kept." )]
	public bool ExcludeUnusedFiles { get; set; }

	/// <summary>
	/// The Steam AppID for the target .exe
	/// </summary>
	[Title( "Steam App ID" )]
	public uint AppId { get; set; }

	/// <summary>
	/// Game's build date
	/// </summary>
	public DateTime BuildDate { get; set; } = DateTime.Now;
}
