using Editor;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace StandaloneTests;

[TestClass]
public class UnusedExportFilesTest
{
	[TestMethod]
	public void PresetsKeepOptionalFilter()
	{
		Assert.IsFalse( JsonSerializer.Deserialize<ExportConfig>( "{}" ).ExcludeUnusedFiles );
		var saved = JsonSerializer.Serialize( new ExportConfig { ExcludeUnusedFiles = true } );
		Assert.IsTrue( JsonSerializer.Deserialize<ExportConfig>( saved ).ExcludeUnusedFiles );
	}

	[TestMethod]
	public void TextReferencesPreserveRelativePathsAndFilenamesWithSpaces()
	{
		string[] files = ["ui/my icon.png", "textures/background.png", "unused.png"];
		var references = UnusedExportFiles.TextReferences( "background: url('my icon.png'); mask: url('../textures/background.png');" );
		CollectionAssert.AreEquivalent( new[] { "ui/my icon.png", "textures/background.png" }, UnusedExportFiles.FindUsed( files, [], references, _ => [] ).ToArray() );
	}

	[TestMethod]
	public void TracesDependenciesAndExplicitResourcesWithoutKeepingDeadReferences()
	{
		string[] files = ["start.scene_c", "models/used.vmdl_c", "materials/used.vmat_c", "textures/used.vtex_c", "models/old.vmdl_c", "textures/old.vtex_c", "notes.md", "balance.txt", "ui/manual.md"];
		var edges = new Dictionary<string, string[]>
		{
			["start.scene_c"] = ["models/used.vmdl"],
			["models/used.vmdl_c"] = ["materials/used.vmat"],
			["materials/used.vmat_c"] = ["textures/used.vtex"],
			["models/old.vmdl_c"] = ["textures/old.vtex"],
		};
		var used = UnusedExportFiles.FindUsed( files, ["start.scene_c", "ui/*"],
			UnusedExportFiles.CodeReferences( "var text = FileSystem.Mounted.ReadAllText(\"balance.txt\");" ), name => edges.GetValueOrDefault( name, [] ) );
		CollectionAssert.AreEquivalent( new[] { "start.scene_c", "models/used.vmdl_c", "materials/used.vmat_c", "textures/used.vtex_c", "balance.txt", "ui/manual.md" }, used.ToArray() );
	}

	[TestMethod]
	public void DynamicPathsKeepTheirScopeAndUnknownReadsKeepEverything()
	{
		string[] files = ["icons/a.png", "icons/b.png", "other.png", "notes.md"];
		var references = UnusedExportFiles.CodeReferences( "var label = $\"{score}\"; var image = Texture.Load($\"icons/{name}.png\");" );
		var used = UnusedExportFiles.FindUsed( files, [], references, _ => [] );
		CollectionAssert.AreEquivalent( new[] { "icons/a.png", "icons/b.png" }, used.ToArray() );
		var unknown = UnusedExportFiles.CodeReferences( "var data = FileSystem.Mounted.ReadAllText(path);" );
		CollectionAssert.AreEquivalent( files, UnusedExportFiles.FindUsed( files, [], unknown, _ => [] ).ToArray() );
		var mutable = UnusedExportFiles.CodeReferences( "var path = \"icons/a.png\"; path = input; var data = File.ReadAllText(path);" );
		CollectionAssert.AreEquivalent( files, UnusedExportFiles.FindUsed( files, [], mutable, _ => [] ).ToArray() );
		var directory = UnusedExportFiles.CodeReferences( "var paths = FileSystem.Mounted.FindFile(\"icons\");" );
		CollectionAssert.AreEquivalent( new[] { "icons/a.png", "icons/b.png" }, UnusedExportFiles.FindUsed( files, [], directory, _ => [] ).ToArray() );
	}

	[TestMethod]
	public void UnknownResourceLoadsKeepResourcesAndPathCombineKeepsMatchingFiles()
	{
		string[] files = ["models/a.vmdl_c", "other.vmat_c", "notes.md", "images/a.png"];
		var unknown = UnusedExportFiles.CodeReferences( "var model = Model.Load(path);" );
		CollectionAssert.AreEquivalent( new[] { "models/a.vmdl_c", "other.vmat_c", "images/a.png" }, UnusedExportFiles.FindUsed( files, [], unknown, _ => [] ).ToArray() );
		var discovered = UnusedExportFiles.CodeReferences( "var items = ResourceLibrary.GetAll<Item>();" );
		CollectionAssert.AreEquivalent( new[] { "models/a.vmdl_c", "other.vmat_c" }, UnusedExportFiles.FindUsed( files, [], discovered, _ => [] ).ToArray() );
		var combine = UnusedExportFiles.CodeReferences( "var image = Texture.Load(Path.Combine(\"images\", name + \".png\"));" );
		CollectionAssert.AreEquivalent( new[] { "images/a.png" }, UnusedExportFiles.FindUsed( files, [], combine, _ => [] ).ToArray() );
	}
}
