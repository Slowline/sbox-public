using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Editor;

/// <summary>
/// Conservative reference tracing for an opt-in standalone export.
/// </summary>
internal static class UnusedExportFiles
{
	internal static string Normalize( string path ) => path.Replace( '\\', '/' ).TrimStart( '/' ).ToLowerInvariant();
	internal static string SourcePath( string path ) => path.EndsWith( "_c", StringComparison.OrdinalIgnoreCase ) ? path[..^2] : path;

	internal static IEnumerable<string> TextReferences( string text )
	{
		foreach ( Match match in Regex.Matches( text, @"[\w./\\:-]+\.[\w]+" ) ) yield return match.Value;
		foreach ( Match match in Regex.Matches( text, "\"([^\"\\r\\n]*)\"|'([^'\\r\\n]*)'|url\\(\\s*([^)]*)\\)" ) )
			foreach ( Group group in match.Groups.Cast<Group>().Skip( 1 ).Where( x => x.Success ) )
				yield return group.Value.Trim().Trim( '\'', '"' );
	}

	internal static HashSet<string> CodeReferences( string text )
	{
		var syntax = CSharpSyntaxTree.ParseText( text ).GetRoot();
		var comments = syntax.DescendantTrivia().Where( x => x.IsKind( SyntaxKind.SingleLineCommentTrivia ) || x.IsKind( SyntaxKind.MultiLineCommentTrivia ) ||
			x.IsKind( SyntaxKind.SingleLineDocumentationCommentTrivia ) || x.IsKind( SyntaxKind.MultiLineDocumentationCommentTrivia ) );
		var code = syntax.ReplaceTrivia( comments, ( _, _ ) => default ).ToFullString();
		// Wildcards are meaningful only at file/resource accesses, not in arbitrary text (e.g. "all" or "*").
		var references = new HashSet<string>( TextReferences( code ).Where( x => !x.Contains( '*' ) && !x.Contains( '?' ) ), StringComparer.OrdinalIgnoreCase );
		foreach ( var literal in syntax.DescendantNodes().OfType<LiteralExpressionSyntax>() )
			if ( literal.Token.Value is string value && !value.Contains( '*' ) && !value.Contains( '?' ) ) references.Add( value );
		foreach ( var call in syntax.DescendantNodes().OfType<InvocationExpressionSyntax>() )
		{
			var member = call.Expression as MemberAccessExpressionSyntax;
			if ( member is null ) continue;
			var method = member.Name.Identifier.ValueText;
			if ( member.Expression.ToString().EndsWith( "ResourceLibrary", StringComparison.Ordinal ) && method.StartsWith( "Get", StringComparison.Ordinal ) )
			{
				// Resources can be discovered by type/name without a file path in code.
				references.Add( "*_c" );
				continue;
			}
			if ( method is not ("Load" or "LoadAsync" or "LoadFromFile" or "ReadAllText" or "ReadAllBytes" or "ReadAllLines" or "ReadAllTextAsync" or "ReadAllBytesAsync" or "ReadJson" or "OpenRead" or "FindFile" or "FindDirectory" or "GetTexture" or "FileExists" or "DirectoryExists" or "EnumerateFiles" or "GetFiles") ) continue;
			if ( member.Expression.ToString().StartsWith( "FileSystem.Data", StringComparison.Ordinal ) ) continue;
			if ( call.ArgumentList.Arguments.Count == 0 ) continue;
			var argument = call.ArgumentList.Arguments[0].Expression;
			// Only constants are safe to resolve: mutable paths can change after their initializer.
			if ( argument is IdentifierNameSyntax identifier )
			{
				var declarations = syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>()
					.Where( x => x.Identifier.ValueText == identifier.Identifier.ValueText ).ToArray();
				if ( declarations.Length == 1 && declarations[0].Initializer is { } initializer &&
					(declarations[0].Parent?.Parent is LocalDeclarationStatementSyntax local && local.Modifiers.Any( SyntaxKind.ConstKeyword ) ||
					declarations[0].Parent?.Parent is FieldDeclarationSyntax field && field.Modifiers.Any( SyntaxKind.ConstKeyword )) ) argument = initializer.Value;
			}
			var argumentPattern = Pattern( argument );
			if ( method is "FindFile" or "FindDirectory" or "DirectoryExists" or "EnumerateFiles" or "GetFiles" )
				argumentPattern = argumentPattern.Trim( '*' ).Length == 0 ? "*" : argumentPattern.TrimEnd( '/', '\\' ) + "/*";
			if ( argumentPattern.Trim( '*' ).Length == 0 && method is "Load" or "LoadAsync" or "GetTexture" )
			{
				// Unknown resource names may select any compiled asset or raw image/audio/video.
				references.UnionWith( ["*_c", "*.vpk", "*.png", "*.jpg", "*.jpeg", "*.tga", "*.gif", "*.svg", "*.webp", "*.wav", "*.mp3", "*.ogg", "*.mp4"] );
			}
			else references.Add( argumentPattern );
		}
		return references;
	}

	static string Pattern( ExpressionSyntax expression ) => expression switch
	{
		LiteralExpressionSyntax literal when literal.Token.Value is string value => value,
		InterpolatedStringExpressionSyntax interpolated => string.Concat( interpolated.Contents.Select( x => x is InterpolatedStringTextSyntax text ? text.TextToken.ValueText : "*" ) ),
		BinaryExpressionSyntax addition when addition.IsKind( SyntaxKind.AddExpression ) => Pattern( addition.Left ) + Pattern( addition.Right ),
		InvocationExpressionSyntax call when call.Expression.ToString() is "Path.Combine" or "System.IO.Path.Combine" => string.Join( "/", call.ArgumentList.Arguments.Select( x => Pattern( x.Expression ) ) ),
		_ => "*"
	};

	internal static HashSet<string> FindUsed( IEnumerable<string> files, IEnumerable<string> roots,
		IEnumerable<string> references, Func<string, IEnumerable<string>> dependencies )
	{
		var names = files.Select( Normalize ).ToHashSet( StringComparer.OrdinalIgnoreCase );
		var used = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var pending = new Queue<string>();
		var matched = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		void Match( string reference )
		{
			if ( string.IsNullOrWhiteSpace( reference ) ) return;
			var pattern = Normalize( reference );
			if ( pattern.StartsWith( "./" ) || pattern.StartsWith( "../" ) ) pattern = Path.GetFileName( pattern );
			if ( !matched.Add( pattern ) ) return;
			if ( !pattern.Contains( '*' ) && !pattern.Contains( '?' ) && pattern.Contains( '/' ) && !pattern.EndsWith( '/' ) )
			{
				foreach ( var name in new[] { pattern, pattern + "_c" } )
					if ( names.Contains( name ) && used.Add( name ) ) pending.Enqueue( name );
				return;
			}
			foreach ( var name in names )
			{
				var source = SourcePath( name );
				if ( name.WildcardMatch( pattern ) || source.WildcardMatch( pattern ) ||
					(!pattern.Contains( '/' ) && Path.GetFileName( source ).WildcardMatch( pattern )) ||
					(pattern.EndsWith( '/' ) && name.StartsWith( pattern, StringComparison.OrdinalIgnoreCase )) )
					if ( used.Add( name ) ) pending.Enqueue( name );
			}
		}
		foreach ( var root in roots.Concat( references ) ) Match( root );
		while ( pending.TryDequeue( out var name ) )
			foreach ( var dependency in dependencies( name ) ) Match( dependency );
		return used;
	}
}
