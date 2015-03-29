using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SoapClientGenerator.Roslyn
{
	public static class SyntaxTreeExtensions
	{
		#region WithModifiers

		public static FieldDeclarationSyntax WithModifiers(this FieldDeclarationSyntax typeSymbol, params SyntaxKind[] modifiers)
		{
			var tokenList = new SyntaxTokenList();
			foreach (var modifier in modifiers)
			{
				tokenList = tokenList.Add(SyntaxFactory.Token(modifier));
			}
			return typeSymbol.WithModifiers(tokenList);
		}

		public static PropertyDeclarationSyntax WithModifiers(this PropertyDeclarationSyntax typeSymbol, params SyntaxKind[] modifiers)
		{
			var tokenList = new SyntaxTokenList();
			foreach (var modifier in modifiers)
			{
				tokenList = tokenList.Add(SyntaxFactory.Token(modifier));
			}
			return typeSymbol.WithModifiers(tokenList);
		}

		public static ClassDeclarationSyntax WithModifiers(this ClassDeclarationSyntax typeSymbol, params SyntaxKind[] modifiers)
		{
			var tokenList = new SyntaxTokenList();
			foreach (var modifier in modifiers)
			{
				tokenList = tokenList.Add(SyntaxFactory.Token(modifier));
			}
			return typeSymbol.WithModifiers(tokenList);
		}

		public static MethodDeclarationSyntax WithModifiers(this MethodDeclarationSyntax typeSymbol, params SyntaxKind[] modifiers)
		{
			var tokenList = new SyntaxTokenList();
			foreach (var modifier in modifiers)
			{
				tokenList = tokenList.Add(SyntaxFactory.Token(modifier));
			}
			return typeSymbol.WithModifiers(tokenList);
		}

		public static EnumDeclarationSyntax WithModifiers(this EnumDeclarationSyntax typeSymbol, params SyntaxKind[] modifiers)
		{
			var tokenList = new SyntaxTokenList();
			foreach (var modifier in modifiers)
			{
				tokenList = tokenList.Add(SyntaxFactory.Token(modifier));
			}
			return typeSymbol.WithModifiers(tokenList);
		}

		public static InterfaceDeclarationSyntax WithModifiers(this InterfaceDeclarationSyntax typeSymbol, params SyntaxKind[] modifiers)
		{
			var tokenList = new SyntaxTokenList();
			foreach (var modifier in modifiers)
			{
				tokenList = tokenList.Add(SyntaxFactory.Token(modifier));
			}
			return typeSymbol.WithModifiers(tokenList);
		}

		#endregion

		public static ClassDeclarationSyntax WithBaseList(this ClassDeclarationSyntax syntax, params string[] baseList)
		{
			var baseListSyntax = SyntaxFactory.BaseList();

			foreach (var baseItem in baseList)
			{
				var baseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(baseItem));

				baseListSyntax = baseListSyntax.AddTypes(baseType);
			}
			return syntax.WithBaseList(baseListSyntax);
		}

		public static NamespaceDeclarationSyntax AddUsings(this NamespaceDeclarationSyntax namespaceDeclaration, params string[] usings)
		{
			foreach (var usingName in usings)
			{
				var name = SyntaxFactory.ParseName(usingName);
				var usingSyntax = SyntaxFactory.UsingDirective(name);
				namespaceDeclaration = namespaceDeclaration.AddUsings(usingSyntax);
			}
			return namespaceDeclaration;
		}

		
		public static AttributeSyntax AddArgument(this AttributeSyntax attribute, string name, object value)
		{
			return AddFormattedArgument(attribute, name, value, "{0} = {1}");
		}

		public static AttributeSyntax AddQuotedArgument(this AttributeSyntax attribute, string name, object value)
		{
			return AddFormattedArgument(attribute, name, value, "{0} = \"{1}\"");
		}

		public static AttributeSyntax AddArgument(this AttributeSyntax attribute, object value)
		{
			var expression = SyntaxFactory.ParseExpression(value.ToString());
            var argument = SyntaxFactory.AttributeArgument(expression);
			return attribute.AddArgumentListArguments(argument);
		}

		public static AttributeSyntax AddQuotedArgument(this AttributeSyntax attribute, object value)
		{
			var expression = SyntaxFactory.ParseExpression(string.Format("\"{0}\"", value));
			var argument = SyntaxFactory.AttributeArgument(expression);
			return attribute.AddArgumentListArguments(argument);
		}

		public static AttributeSyntax AddFormattedArgument(this AttributeSyntax attribute, string name, object value, string format)
		{
			ExpressionSyntax expression;
			if (value == null)
			{
				expression = SyntaxFactory.ParseExpression(string.Format("{0} = null", name));
			}
			else
			{
				expression = SyntaxFactory.ParseExpression(string.Format(format, name, value));
			}
			var argument = SyntaxFactory.AttributeArgument(expression);
			return attribute.AddArgumentListArguments(argument);
		}

		public static ClassDeclarationSyntax AddAttribute(this ClassDeclarationSyntax classDeclaration, AttributeSyntax attribute)
		{
			var attributeList = SyntaxFactory.AttributeList().AddAttributes(attribute);
			return classDeclaration.AddAttributeLists(attributeList);
		}

		public static PropertyDeclarationSyntax AddAttribute(this PropertyDeclarationSyntax propertyDeclaration, AttributeSyntax attribute)
		{
			var attributeList = SyntaxFactory.AttributeList().AddAttributes(attribute);
			return propertyDeclaration.AddAttributeLists(attributeList);
		}

		public static FieldDeclarationSyntax AddAttribute(this FieldDeclarationSyntax fieldDeclaration, AttributeSyntax attribute)
		{
			var attributeList = SyntaxFactory.AttributeList().AddAttributes(attribute);
			return fieldDeclaration.AddAttributeLists(attributeList);
		}

		public static EnumMemberDeclarationSyntax AddAttribute(this EnumMemberDeclarationSyntax enumMemberDeclaration, AttributeSyntax attribute)
		{
			var attributeList = SyntaxFactory.AttributeList().AddAttributes(attribute);
			return enumMemberDeclaration.AddAttributeLists(attributeList);
		}

		public static NamespaceDeclarationSyntax NamespaceDeclaration(string name)
		{
			return SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(name));
		}

		public static AttributeSyntax Attribute(string name)
		{
			return SyntaxFactory.Attribute(SyntaxFactory.ParseName(name));
		}

		public static FieldDeclarationSyntax FieldDeclaration(TypeSyntax type, string name)
		{
			var variable = SyntaxFactory.VariableDeclarator(name);
			var variableDecl = SyntaxFactory.VariableDeclaration(type)
				.WithVariables(SyntaxFactory.SeparatedList(new[] {variable}));
            return SyntaxFactory.FieldDeclaration(variableDecl);
		}
	}

	public static class SemanticTreeExtensions
	{
		#region ITypeSymbol.Get...

		public static IEnumerable<IFieldSymbol> GetFields(this ITypeSymbol typeSymbol)
		{
			return typeSymbol.GetMembers().Where(m => m.Kind == SymbolKind.Field).Cast<IFieldSymbol>();
		}

		public static IEnumerable<IPropertySymbol> GetProperties(this ITypeSymbol typeSymbol)
		{
			return typeSymbol.GetMembers().Where(m => m.Kind == SymbolKind.Property).Cast<IPropertySymbol>();
		}

		public static IEnumerable<IMethodSymbol> GetMethods(this ITypeSymbol typeSymbol)
		{
			return typeSymbol.GetMembers().Where(m => m.Kind == SymbolKind.Method).Cast<IMethodSymbol>();
		}

		public static IEnumerable<IEventSymbol> GetEvents(this ITypeSymbol typeSymbol)
		{
			return typeSymbol.GetMembers().Where(m => m.Kind == SymbolKind.Event).Cast<IEventSymbol>();
		}
		#endregion

		public static AttributeData GetAttribute(this ISymbol symbol, string attribute)
		{
			return symbol.GetAttributes().FirstOrDefault(attr => attr.AttributeClass.Name == attribute);
		}

		public static IEnumerable<AttributeData> GetAttributes(this ISymbol symbol, string attribute)
		{
			return symbol.GetAttributes().Where(attr => attr.AttributeClass.Name == attribute);
		}

		public static TypedConstant? GetNamedArgument(this AttributeData attribute, string argumentName)
		{
			var argument = attribute.NamedArguments.FirstOrDefault(item => item.Key == argumentName);

			if (argument.Equals(default(KeyValuePair<string, TypedConstant>))) return null;

			return argument.Value;
		}

		public static T GetValueOrDefault<T>(this TypedConstant? constant)
		{
			if (constant == null) return default(T);

			return (T)constant.Value.Value;
		}

		private static readonly HashSet<string> PrimitiveTypeNames = new HashSet<string>
			{
				"byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double", "decimal", "char", "string", "bool", "object"
			};

		public static bool IsPrimitive(this ITypeSymbol type)
		{
			return PrimitiveTypeNames.Contains(type.ToString());
		}
	}
}
