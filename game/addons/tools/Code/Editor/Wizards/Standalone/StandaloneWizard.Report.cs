using System.Text;

namespace Editor.Wizards;

partial class StandaloneWizard
{
	StandaloneExporter CompletedExporter;
	BaseWindow reportWindow;

	void ShowExportReport()
	{
		if ( CompletedExporter is null ) return;
		reportWindow?.Destroy();
		reportWindow = new BaseWindow
		{
			WindowTitle = "Export Report",
			Size = new Vector2( 900, 650 ),
			MinimumSize = new Vector2( 600, 400 )
		};
		reportWindow.Layout = Layout.Column();
		reportWindow.Layout.Margin = 16;
		reportWindow.Layout.Spacing = 12;
		reportWindow.Layout.Add( new Label.Subtitle( "Export Complete" ) );

		var text = new StringBuilder();
		text.AppendLine( $"Executable: {Config.ExecutableName}.exe" );
		text.AppendLine( $"Version: {Config.ExecutableVersion}" );
		text.AppendLine( $"Export directory: {Config.TargetDir}" );
		text.AppendLine( $"Assets: {(Config.PackAssets ? "VPK" : "Loose files")}" );
		text.AppendLine();
		if ( Config.ExcludeUnusedFiles )
		{
			var excluded = CompletedExporter.ExcludedFiles;
			text.AppendLine( $"Excluded files: {excluded.Count:N0}" );
			text.AppendLine( $"Omitted content: {excluded.Sum( x => (long)x.Size ).FormatBytes()} ({excluded.Sum( x => (long)x.Size ):N0} bytes before VPK packaging)" );
			text.AppendLine( "Explicit Resources, scenes/maps and runtime essentials were kept." );
			if ( CompletedExporter.HasUnresolvedFileAccess )
				text.AppendLine( "Unknown dynamic file access was found. All files were kept for safety." );
			else if ( CompletedExporter.HasWildcardCodeReferences )
				text.AppendLine( "Dynamic/wildcard code references kept matching resources for safety." );
			text.AppendLine( "Project source files were not deleted." );
			text.AppendLine();
			text.AppendLine( "EXCLUDED FILES — no discovered reference" );
			foreach ( var file in excluded.OrderBy( x => x.Name, StringComparer.OrdinalIgnoreCase ) )
				text.AppendLine( $"{file.Name}    {((long)file.Size).FormatBytes()} ({file.Size:N0} bytes)" );
			if ( excluded.Count == 0 ) text.AppendLine( "No files were excluded." );
		}
		else text.AppendLine( "Unused-file analysis was disabled. No files were excluded by the analysis." );

		var details = reportWindow.Layout.Add( new TextEdit( reportWindow ) { ReadOnly = true, PlainText = text.ToString() }, 1 );
		details.SetStyles( "font-family: Consolas, monospace; padding: 8px;" );
		var buttons = reportWindow.Layout.AddRow();
		buttons.Add( new Button( "Open Export Folder", "folder" ) { Clicked = () => EditorUtility.OpenFolder( Config.TargetDir ) } );
		buttons.AddStretchCell();
		buttons.Add( new Button.Primary( "Close" ) { Clicked = () => reportWindow.Close() } );
		reportWindow.SetModal( true, true );
		reportWindow.Show();
	}
}
