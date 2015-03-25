using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.CodeDom;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.ServiceModel;


namespace SoapClientGenerator
{
	class Program
	{
		private const string ClientBaseClassName = "SoapServices.SoapClientBase";
		private const int HelpHeaderWidth = 30;
		private const string SvcUtilDefaultPath = @"C:\Program Files (x86)\Microsoft SDKs\Windows\v8.1A\bin\NETFX 4.5.1 Tools\SvcUtil.exe";
	    private static SemanticModel _semanticModel;

		static void Main(string[] args)
		{
			try
			{
				Debugger.Launch();
				WriteInfo();

				if (args.Length < 3 || args.Length > 4)
				{
					Console.WriteLine("Wrong number of parameters ({0}), expected 3 or 4", args.Length);
					return;
				}

				var svcutil = SvcUtilDefaultPath;
				var wsdlUri = args[0];
				var outFilePath = args[1];
				var ns = args[2];

				var svcutilParam = GetParameters(args.Skip(3)).FirstOrDefault(p => p.Key.Equals("svcutil", StringComparison.OrdinalIgnoreCase));
				if (svcutilParam != null)
				{
					svcutil = svcutilParam.Value;
				}

				CreateProxy(svcutil, wsdlUri, outFilePath, ns);
			}
			catch (Exception e)
			{
				Console.WriteLine("ERROR: {0}", e.Message);
			}
		}

		private static List<AppParameter> GetParameters(IEnumerable<string> args)
		{
			var result = new List<AppParameter>();

			foreach (var s in args)
			{
				if (!s.StartsWith("/"))
					throw new Exception(String.Format("Bad parameter '{0}'", s));

				var pos = s.IndexOf(':');
				if (pos == -1)
				{
					result.Add(new AppParameter(s.Substring(1)));
				}
				else
				{
					var key = s.Substring(1, pos - 1);
					var value = s.Substring(pos + 1);
					result.Add(new AppParameter(key, value));
				}
			}

			return result;
		}

		private static void WriteInfo()
		{
			Console.WriteLine("SOAP client source code generator");
			Console.WriteLine("SvcUtil.exe from .Net SDK is required");
			Console.WriteLine();

			Console.WriteLine("Syntax: SoapClientGenerator.exe <metadataDocumentPath> <file> <namespace> [/svcutil:<svcutilPath>]");
			Console.WriteLine("<metadataDocumentPath>".PadRight(HelpHeaderWidth) + " - The path to a metadata document (wsdl)");
			Console.WriteLine("<file>".PadRight(HelpHeaderWidth) + " - Output file path");
			Console.WriteLine("<namespace>".PadRight(HelpHeaderWidth) + " - Output file namespace");
			Console.WriteLine("<svcutil>".PadRight(HelpHeaderWidth) + " - SvcUtil.exe path, default {0}", SvcUtilDefaultPath);
			Console.WriteLine();

			Console.WriteLine(
				"Example: SoapClientGenerator.exe \"http://www.onvif.org/onvif/ver10/device/wsdl/devicemgmt.wsdl\" \"C:\\temp\\devicemgmt.wsdl.cs\" OnvifServices.DeviceManagement");
			Console.WriteLine(
				"Example: SoapClientGenerator.exe \"http://www.onvif.org/onvif/ver10/device/wsdl/devicemgmt.wsdl\" \"C:\\temp\\devicemgmt.wsdl.cs\" OnvifServices.DeviceManagement /svcutil:\"C:\\Program Files (x86)\\Microsoft SDKs\\Windows\\v8.0A\\bin\\NETFX 4.0 Tools\\SvcUtil.exe\"");
			Console.WriteLine();
		}

		private static void CreateProxy(string svcutil, string wsdlUri, string outFilePath, string ns)
		{
			Console.WriteLine("SvcUtil {0}", svcutil);
			Console.WriteLine("WSDL {0}", wsdlUri);
			Console.WriteLine("Code file {0}", outFilePath);
			Console.WriteLine("Code file namespace {0}", ns);
			Console.WriteLine();

			Console.WriteLine("Processing WSDL {0}", wsdlUri);
			// 1. generate raw code
			var csFilePath = Path.GetTempFileName() + ".cs";
			GenerateRawCode(svcutil, wsdlUri, csFilePath);
			Console.WriteLine("Raw file: {0}", csFilePath);

            // 2. create syntax tree and semantic tree
            SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(File.ReadAllText(csFilePath));
            
            var mscorlib = MetadataReference.CreateFromAssembly(typeof(object).Assembly);
            var serialization = MetadataReference.CreateFromAssembly(typeof(ServiceContractAttribute).Assembly);

		    var compilation = CSharpCompilation.Create("ServiceReference", new[] {syntaxTree}, new[] {mscorlib, serialization});
		    _semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetRoot();
            
            // 3. Parse trees
			Console.WriteLine("Gathering info...");
            var metadata = ParseTree(root, _semanticModel);

			// 4. Generate source code
			Console.WriteLine("Code generation...");
            var newRoot = GeneratedCode(metadata, outFilePath, ns);
            File.WriteAllText(outFilePath, newRoot.ToFullString());
            

			Console.WriteLine("DONE!");
			Console.WriteLine();
		}

        private static MetadataInfo ParseTree(SyntaxNode root, SemanticModel semanticModel)
        {
            var visitor = new CollectMetadataSyntaxWalker(semanticModel);

            visitor.Visit(root);

            return visitor.GetCollectedMetadata();
        }

        private static AssemblyInfo ParseAssembly(Assembly assembly)
        {
            var services = from t in assembly.DefinedTypes
                           let serviceContractAttr = t.GetCustomAttribute<ServiceContractAttribute>()
                           where serviceContractAttr != null
                           select new ServiceInterface(t);

            var contracts = from t in assembly.DefinedTypes
                            let serviceContractAttr = t.GetCustomAttribute<ServiceContractAttribute>()
                            where serviceContractAttr == null
                            && !t.ImplementedInterfaces.Contains(typeof(IClientChannel))
                            && !t.BaseType.IsSubclassOf(typeof(ClientBase<>))
                            && !(t.BaseType.Namespace == typeof(ClientBase<>).Namespace && t.BaseType.Name == typeof(ClientBase<>).Name)
                            && !t.IsEnum
                            select new ContractInfo(t);

            var enums = from t in assembly.DefinedTypes
                        where t.IsEnum
                        select new EnumInfo(t);

            return new AssemblyInfo()
            {
                Services = new List<ServiceInterface>(services),
                Contracts = new List<ContractInfo>(contracts),
                Enums = new List<EnumInfo>(enums)
            };
        }



	    private static NamespaceDeclarationSyntax GeneratedCode(MetadataInfo metadataInfo, string srcFile, string ns)
	    {
	        var nameSpace = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(ns));
            nameSpace = nameSpace
                .WithName(nameSpace.Name.WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed))
                .WithOpenBraceToken(nameSpace.OpenBraceToken.WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed))
                .WithCloseBraceToken(nameSpace.CloseBraceToken .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));
            nameSpace = nameSpace.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Xml.Serialization").WithLeadingTrivia(SyntaxFactory.Space)).WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));
            
            foreach (var serviceInterface in metadataInfo.Services)
	        {
                var interfaceDeclarationSyntax = AddClientInterface(serviceInterface);
	            nameSpace = nameSpace.AddMembers(interfaceDeclarationSyntax);

	            var classDeclarationSyntax = AddClientImplementation(serviceInterface);
                nameSpace = nameSpace.AddMembers(classDeclarationSyntax);
	        }

            foreach (var contractInfo in metadataInfo.Contracts)
            {
                var codeDeclaration = AddDataContract(contractInfo);
                nameSpace = nameSpace.AddMembers(codeDeclaration);
            }

            foreach (var enumInfo in metadataInfo.Enums)
            {
                var enumDeclSyntax = AddEnum(enumInfo);
                nameSpace = nameSpace.AddMembers(enumDeclSyntax);
            }

	        return nameSpace;
	    }

	    private static ClassDeclarationSyntax AddDataContract(INamedTypeSymbol classInfo)
	    {
	        var classDecl = SyntaxFactory.ClassDeclaration(classInfo.Name);
            classDecl = classDecl
                .WithIdentifier(classDecl.Identifier.WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed))
                .WithModifiers(SyntaxTokenList.Create(SyntaxFactory.ParseToken("public").WithTrailingTrivia(SyntaxFactory.Space)))
                .WithOpenBraceToken(classDecl.OpenBraceToken.WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed))
                .WithCloseBraceToken(classDecl.CloseBraceToken.WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));

	        if (classInfo.BaseType.Name != "Object")
	        {
                var baseTypeList = SyntaxFactory.BaseList().AddTypes(SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(classInfo.BaseType.ToString())));
                classDecl = classDecl.WithBaseList(baseTypeList);
	        }

	        var xmlTypeAttr = classInfo.GetAttributes().FirstOrDefault(attr => attr.AttributeClass.Name == "XmlTypeAttribute");
            var messageContractAttr = classInfo.GetAttributes().FirstOrDefault(attr => attr.AttributeClass.Name == "MessageContractAttribute");//Info.GetCustomAttribute<MessageContractAttribute>();

            bool addXmlRoot = false;
            string xmlRootElementName = null;
            string xmlRootNamespace = null;

            if (xmlTypeAttr != null)
            {
                addXmlRoot = true;
                xmlRootNamespace = xmlTypeAttr.NamedArguments.First(item => item.Key == "Namespace").Value.Value as string;
            }
            else
            {
                if (messageContractAttr != null)
                {
                    addXmlRoot = true;
                    xmlRootElementName = messageContractAttr.NamedArguments.First(item => item.Key == "WrapperName").Value.Value as string;
                    xmlRootNamespace = messageContractAttr.NamedArguments.First(item => item.Key == "WrapperNamespace").Value.Value as string;
                }
            }

            var bodyMember = classInfo.GetMembers().FirstOrDefault(m => m.Kind == SymbolKind.Field && m.GetAttributes().Any(attr => attr.AttributeClass.Name == "MessageBodyMemberAttribute")); //Info.DeclaredFields.FirstOrDefault(f => f.IsPublic && f.GetCustomAttribute<MessageBodyMemberAttribute>() != null);
            if (bodyMember != null)
            {
                xmlRootElementName = bodyMember.Name;
                var bodyAttr = bodyMember.GetAttributes().First(attr => attr.AttributeClass.Name == "MessageBodyMemberAttribute");
                xmlRootNamespace = bodyAttr.NamedArguments.GetArgumentValueOrDefault<string>("Namespace");
            }

	        if (addXmlRoot)
	        {
	            var attr = SyntaxFactory.Attribute(SyntaxFactory.ParseName("XmlRootAttribute"));
                var arg1 = SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression(string.Format("{0} = \"{1}\"", "ElementName", xmlRootElementName)));
                var arg2 = SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression(string.Format("{0} = \"{1}\"", "Namespace", xmlRootNamespace)));
	            attr = attr.AddArgumentListArguments(arg1, arg2);
                
                classDecl = classDecl.AddAttributeLists(SyntaxFactory.AttributeList().AddAttributes(attr).WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));
	        }

            classDecl = AddDataContractFields(classDecl, classInfo);
            classDecl = AddDataContractProperties(classDecl, classInfo);


	        return classDecl;
	    }

	    private static ClassDeclarationSyntax AddDataContractProperties(ClassDeclarationSyntax classDecl, INamedTypeSymbol classInfo)
	    {
	        foreach (var propertySymbol in classInfo.GetMembers()
                .Where(m => m.Kind == SymbolKind.Property)
	            .Where(prop => !prop.GetAttributes().Any(attr => attr.AttributeClass.Name == "XmlAnyAttributeAttribute") &&
	                           !prop.GetAttributes().Any(attr => attr.AttributeClass.Name == "XmlIgnoreAttribute")).Cast<IPropertySymbol>())
	        {
                if (propertySymbol.Type.Name == "XmlNode" || propertySymbol.Type.Name == "ExtensionDataObject")
                    continue;

	            var name = propertySymbol.Name == "fixed" ? "@fixed" : propertySymbol.Name;
                var list = SyntaxFactory.AccessorList()
                    .AddAccessors(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.ParseToken(";")))
                    .AddAccessors(SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.ParseToken(";")));

                var propertySyntax = SyntaxFactory.PropertyDeclaration(SyntaxFactory.ParseTypeName(GetTypeName(propertySymbol.Type)).WithTrailingTrivia(SyntaxFactory.Space), name);
                propertySyntax = propertySyntax
                    .WithModifiers(SyntaxTokenList.Create(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space)))
                    .WithAccessorList(list)
                    .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

                var xmlElementAttrs = propertySymbol.GetAttributes().Where(attr => attr.AttributeClass.Name == "XmlElementAttribute");
                foreach (var xmlElementAttr in xmlElementAttrs)
                {
                    var xmlElementAttrSyntax = SyntaxFactory.Attribute(SyntaxFactory.ParseName("XmlElementAttribute"));

                    var elementName = xmlElementAttr.NamedArguments.GetArgumentValueOrDefault<string>("ElementName");
                    if (!String.IsNullOrEmpty(elementName))
                    {
                        var arg = SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression(string.Format("{0} = \"{1}\"", "ElementName", elementName)));
                        xmlElementAttrSyntax = xmlElementAttrSyntax.AddArgumentListArguments(arg);
                    }

                    string ns = GetNamespace(xmlElementAttr, null);
                    if (ns != null)
                    {
                        var arg = SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression(string.Format("{0} = \"{1}\"", "Namespace", ns)));
                        xmlElementAttrSyntax = xmlElementAttrSyntax.AddArgumentListArguments(arg);
                    }

                    string dataType = GetDataType(xmlElementAttr);
                    if (dataType != null)
                    {
                        var arg = SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression(string.Format("{0} = {1}", "DataType", ns)));
                        xmlElementAttrSyntax = xmlElementAttrSyntax.AddArgumentListArguments(arg);
                    }

                    int order = GetOrder(xmlElementAttr, null);
                    var orderArg = SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression(string.Format("{0} = {1}", "Order", order)));
                    xmlElementAttrSyntax = xmlElementAttrSyntax.AddArgumentListArguments(orderArg);

                    propertySyntax = propertySyntax.AddAttributeLists(SyntaxFactory.AttributeList().AddAttributes(xmlElementAttrSyntax).WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));
                }

                var xmlAnyElementAttr = propertySymbol.GetAttributes().FirstOrDefault(attr => attr.AttributeClass.Name == "XmlAnyElementAttribute");
                if (xmlAnyElementAttr != null)
                {
                    var xmlAnyElementAttrSyntax = SyntaxFactory.Attribute(SyntaxFactory.ParseName("XmlAnyElementAttribute"));

                    var ns = xmlAnyElementAttr.NamedArguments.GetArgumentValueOrDefault<string>("Namespace");
                    if (ns != null)
                    {
                        var arg = SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression(string.Format("{0} = \"{1}\"", "Namespace", ns)));
                        xmlAnyElementAttrSyntax = xmlAnyElementAttrSyntax.AddArgumentListArguments(arg);
                    }

                    var order = xmlAnyElementAttr.NamedArguments.GetArgumentValueOrDefault<string>("Order");
                    var orderArg = SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression(string.Format("{0} = {1}", "Order", order)));
                    xmlAnyElementAttrSyntax = xmlAnyElementAttrSyntax.AddArgumentListArguments(orderArg);

                    propertySyntax = propertySyntax.AddAttributeLists(SyntaxFactory.AttributeList().AddAttributes(xmlAnyElementAttrSyntax).WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));
                }

                var xmlAttributeAttr = propertySymbol.GetAttributes().FirstOrDefault(attr => attr.AttributeClass.Name == "XmlAttributeAttribute"); 
                if (xmlAttributeAttr != null)
                {
                    var xmlAttributeAttrSyntax = SyntaxFactory.Attribute(SyntaxFactory.ParseName("XmlAttributeAttribute"));

                    var ns = xmlAttributeAttr.NamedArguments.GetArgumentValueOrDefault<string>("Namespace");
                    if (ns != null)
                    {
                        var arg = SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression(string.Format("{0} = \"{1}\"", "Namespace", ns)));
                        xmlAttributeAttrSyntax = xmlAttributeAttrSyntax.AddArgumentListArguments(arg);
                    }

                    var dataType = xmlAttributeAttr.NamedArguments.GetArgumentValueOrDefault<string>("DataType");
                    if (!string.IsNullOrEmpty(dataType))
                    {
                        var arg = SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression(string.Format("{0} = {1}", "DataType", dataType)));
                        xmlAttributeAttrSyntax = xmlAttributeAttrSyntax.AddArgumentListArguments(arg);
                    }

                    var form = xmlAttributeAttr.NamedArguments.GetArgumentValueOrDefault<string>("Form");
                    if (form != "None")
                    {
                        var arg = SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression(string.Format("{0} = XmlSchemaForm.{1}", "Form", form)));
                        xmlAttributeAttrSyntax = xmlAttributeAttrSyntax.AddArgumentListArguments(arg);
                    }

                    propertySyntax = propertySyntax.AddAttributeLists(SyntaxFactory.AttributeList().AddAttributes(xmlAttributeAttrSyntax).WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));
                }

                var xmlTextAttr = propertySymbol.GetAttributes().FirstOrDefault(attr => attr.AttributeClass.Name == "XmlTextAttribute"); 
                if (xmlTextAttr != null)
                {
                    var xmlTextAttrSyntax = SyntaxFactory.Attribute(SyntaxFactory.ParseName("XmlTextAttribute"));

                    propertySyntax = propertySyntax.AddAttributeLists(SyntaxFactory.AttributeList().AddAttributes(xmlTextAttrSyntax).WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));
                }

	            classDecl = classDecl.AddMembers(propertySyntax);
	        }

	        return classDecl;
	    }

        private static string GetPropertyTypeName(ITypeSymbol type)
        {
            if (type.Name == "XmlElement") return "XElement";
            if (type.Name == "XmlElement[]") return "XElement[]";

            return GetTypeName(type);
        }

	    private static ClassDeclarationSyntax AddDataContractFields(ClassDeclarationSyntax classDecl, INamedTypeSymbol classInfo)
	    {
            foreach (var fieldSymbol in classInfo.GetMembers().Where(m => m.Kind == SymbolKind.Field).Cast<IFieldSymbol>())
	        {
	            var messageBodyMemberAttr = fieldSymbol.GetAttributes().FirstOrDefault(attr => attr.AttributeClass.Name == "MessageBodyMemberAttribute");
	            if (messageBodyMemberAttr == null) continue;

	            TypeSyntax type;
	            if (!fieldSymbol.GetAttributes().Any(attr => attr.AttributeClass.Name == "XmlAnyElementAttribute"))
	            {
	                type = SyntaxFactory.ParseTypeName(GetTypeName(fieldSymbol.Type)); 
	            }
	            else
	            {
	                type = SyntaxFactory.ParseTypeName("XElement[]");
	            }
	            var decl = SyntaxFactory.VariableDeclarator(fieldSymbol.Name).WithLeadingTrivia(SyntaxFactory.Space);
	            
                
	            var fieldSyntax = SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(type).WithVariables(SyntaxFactory.SeparatedList(new[] {decl})));

                var xmlElementAttr = fieldSymbol.GetAttributes().FirstOrDefault(attr => attr.AttributeClass.Name == "XmlElementAttribute"); 

                string elementName = null;
                bool isNullable = true;
                if (xmlElementAttr != null)
                {
                    var elementNameValue = xmlElementAttr.NamedArguments.GetArgumentValueOrDefault<string>("ElementName");
                    if (string.IsNullOrEmpty(elementNameValue))
                    {
                        elementName = elementNameValue;
                    }
                }

                var xmlArrayItemAttribute = fieldSymbol.GetAttributes().FirstOrDefault(attr => attr.AttributeClass.Name == "XmlArrayItemAttribute"); 
                if (xmlArrayItemAttribute != null)
                {
                    elementName = xmlArrayItemAttribute.NamedArguments.GetArgumentValueOrDefault<string>("ElementName");
                    isNullable = xmlArrayItemAttribute.NamedArguments.GetArgumentValueOrDefault<bool>("IsNullable");
                }

	            
	            var args = SyntaxFactory.AttributeArgumentList();
                args = args.AddArguments(SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression(string.Format("{0} = {1}", "ElementName", string.IsNullOrEmpty(elementName) ? "null" : "\"" + elementName + "\""))));
                args = args.AddArguments(SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression(string.Format("{0} = {1}", "IsNullable", isNullable.ToString().ToLower()))));

                string ns = GetNamespace(xmlElementAttr, messageBodyMemberAttr);
                if (ns != null)
                {
                    args = args.AddArguments(SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression(string.Format("{0} = \"{1}\"", "Namespace", ns))));
                }

                string dataType = GetDataType(xmlElementAttr);
                if (dataType != null)
                {
                    args = args.AddArguments(SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression(string.Format("{0} = {1}", "DataType", dataType))));
                }

                int order = GetOrder(xmlElementAttr, messageBodyMemberAttr);
                args = args.AddArguments(SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression(string.Format("{0} = {1}", "Order", order))));

	            var xmlElementAttrSyntax = SyntaxFactory.Attribute(SyntaxFactory.ParseName("XmlElementAttribute")).WithArgumentList(args);
                fieldSyntax = fieldSyntax
                    .AddAttributeLists(SyntaxFactory.AttributeList().AddAttributes(xmlElementAttrSyntax).WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed))
                    .WithModifiers(SyntaxTokenList.Create(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space)))
                    .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
	            classDecl = classDecl.AddMembers(fieldSyntax);

	        }

	        return classDecl;
	    }

	    private static string GetTypeName(ITypeSymbol type)
	    {
            List<string> elementar = new List<string>
            {
                "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double", "decimal", "char", "string", "bool", "object"
            };
	        var name = type.ToString();
	        if (elementar.Contains(name)) return name;

	        return type.Name;
	    }

        private static string GetNamespace(AttributeData xmlElementAttr, AttributeData messageBodyMemberAttr)
        {
            if (xmlElementAttr != null)
            {
                var ns = xmlElementAttr.NamedArguments.GetArgumentValueOrDefault<string>("Namespace");
                if (ns != null) return ns;
            }

            if (messageBodyMemberAttr != null)
            {
                var ns = messageBodyMemberAttr.NamedArguments.GetArgumentValueOrDefault<string>("Namespace");
                if (ns != null) return ns;
            }

            return null;
        }

        private static string GetDataType(AttributeData xmlElementAttr)
        {
            if (xmlElementAttr == null) return null;
            return xmlElementAttr.NamedArguments.GetArgumentValueOrDefault<string>("DataType");
        }

        private static int GetOrder(AttributeData xmlElementAttr, AttributeData messageBodyMemberAttr)
        {
            if (xmlElementAttr != null)
            {
                var order = xmlElementAttr.NamedArguments.GetArgumentValueOrDefault<int>("Order");
                if (order > 0) return order;
            }
            if (messageBodyMemberAttr != null)
            {
                var order = messageBodyMemberAttr.NamedArguments.GetArgumentValueOrDefault<int>("Order");
                if (order > 0) return order;
            }

            return 0;
        }

	    private static EnumDeclarationSyntax AddEnum(INamedTypeSymbol enumInfo)
	    {
	        var enumDecl = SyntaxFactory.EnumDeclaration(enumInfo.Name);
	        enumDecl = enumDecl
	            .WithModifiers(SyntaxTokenList.Create(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space)))
	            .WithIdentifier(enumDecl.Identifier.WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed))
                .WithCloseBraceToken(enumDecl.CloseBraceToken.WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed).WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));

	        foreach (var member in enumInfo.GetMembers().Where(m => m.Kind == SymbolKind.Field))
	        {
	            var memberSyntax = SyntaxFactory.EnumMemberDeclaration(member.Name).WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed);
                enumDecl = enumDecl.AddMembers(memberSyntax);
	        }

	        return enumDecl;
	    }

	    private static void GeneratedCode(AssemblyInfo assemblyInfo, string srcFile, string ns)
        {
            var targetUnit = new CodeCompileUnit();
            var samples = new CodeNamespace(ns);
            //samples.Imports.Add(new CodeNamespaceImport("System"));

            targetUnit.Namespaces.Add(samples);

            foreach (var serviceInterface in assemblyInfo.Services)
            {
                var interfaceTypeDec = AddClientInterface(serviceInterface);
                samples.Types.Add(interfaceTypeDec);

                var implTypeDec = AddClientImplementation(serviceInterface);
                samples.Types.Add(implTypeDec);
            }

            foreach (var contractInfo in assemblyInfo.Contracts)
            {
                var codeDeclaration = contractInfo.GetCodeDeclaration();
                samples.Types.Add(codeDeclaration);
            }

            foreach (var enumInfo in assemblyInfo.Enums)
            {
                var codeDeclaration = enumInfo.GetCodeDeclaration();
                samples.Types.Add(codeDeclaration);
            }

            var provider = CodeDomProvider.CreateProvider("CSharp");
            var options = new CodeGeneratorOptions();
            options.BracingStyle = "C";
            options.BlankLinesBetweenMembers = true;
            using (var sourceWriter = new StreamWriter(srcFile))
            {
                provider.GenerateCodeFromCompileUnit(targetUnit, sourceWriter, options);
            }

            // Fix auto props declaration
            var srcContent = File.ReadAllText(srcFile);
            srcContent = srcContent.Replace("};", "}");
            File.WriteAllText(srcFile, srcContent);
        }

        

        private static InterfaceDeclarationSyntax AddClientInterface(INamedTypeSymbol serviceInterface)
        {
            var interfaceDeclarationSyntax = SyntaxFactory.InterfaceDeclaration(serviceInterface.Name);
            
            interfaceDeclarationSyntax = interfaceDeclarationSyntax
                .WithIdentifier(interfaceDeclarationSyntax.Identifier.WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed))
                .WithModifiers(SyntaxTokenList.Create(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space)))
                .WithOpenBraceToken(interfaceDeclarationSyntax.OpenBraceToken.WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed))
                .WithCloseBraceToken(interfaceDeclarationSyntax.CloseBraceToken.WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));
            
            var asyncOperationContracts = serviceInterface.GetMembers()
                .Cast<IMethodSymbol>()
                .Where(item => ((INamedTypeSymbol)item.ReturnType).IsGenericType && item.ReturnType.Name == "Task")
                .Where(item => item.GetAttributes().Any(attr => attr.AttributeClass.Name == "OperationContractAttribute"));


            foreach (var serviceMethod in asyncOperationContracts)
            {
                var returnType =  SyntaxFactory.ParseTypeName(serviceMethod.ReturnType.ToString());
                var method = SyntaxFactory.MethodDeclaration(returnType, serviceMethod.Name);
                method = method
                    .WithIdentifier(method.Identifier.WithLeadingTrivia(SyntaxFactory.Space))
                    .WithSemicolonToken(SyntaxFactory.ParseToken(";"))
                    .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
                
                foreach (var parameter in serviceMethod.Parameters)
                {
                    var parameterType = SyntaxFactory.ParseTypeName(parameter.Type.ToString());
                    
                    var nameToken = SyntaxFactory.ParseToken(parameter.Name);

                    var param = SyntaxFactory.Parameter(nameToken);
                    param = param.WithIdentifier(param.Identifier.WithLeadingTrivia(SyntaxFactory.Space)).WithType(parameterType);

                    method = method.AddParameterListParameters(param);
                }

                interfaceDeclarationSyntax = interfaceDeclarationSyntax.AddMembers(method);
            }

            return interfaceDeclarationSyntax;
        }

        private static CodeTypeDeclaration AddClientInterface(ServiceInterface serviceInterface)
        {
            var interfaceTypeDec = new CodeTypeDeclaration(serviceInterface.Name);
            interfaceTypeDec.IsInterface = true;

            foreach (var serviceMethod in serviceInterface.Methods)
            {
                var mth = new CodeMemberMethod();
                mth.Name = serviceMethod.Name;
                mth.ReturnType = new CodeTypeReference(serviceMethod.MethodInfo.ReturnType);

                foreach (var parameterInfo in serviceMethod.MethodInfo.GetParameters())
                {
                    mth.Parameters.Add(new CodeParameterDeclarationExpression(parameterInfo.ParameterType, parameterInfo.Name));
                }

                interfaceTypeDec.Members.Add(mth);
            }
            return interfaceTypeDec;
        }
        


        private static ClassDeclarationSyntax AddClientImplementation(INamedTypeSymbol serviceInterface)
        {
            var classDeclarationSyntax = SyntaxFactory.ClassDeclaration(serviceInterface.Name + "Client");

            classDeclarationSyntax = classDeclarationSyntax
                .WithIdentifier(classDeclarationSyntax.Identifier.WithLeadingTrivia(SyntaxFactory.Space))
                .WithModifiers(SyntaxTokenList.Create(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space)).Add(SyntaxFactory.Token(SyntaxKind.PartialKeyword).WithTrailingTrivia(SyntaxFactory.Space)))
                .WithOpenBraceToken(classDeclarationSyntax.OpenBraceToken.WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed))
                .WithCloseBraceToken(classDeclarationSyntax.CloseBraceToken.WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));

            var interfaceBaseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(serviceInterface.Name)).WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
            var clientBaseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(ClientBaseClassName));
            var baseList = SyntaxFactory.BaseList().AddTypes(clientBaseType, interfaceBaseType);
            classDeclarationSyntax = classDeclarationSyntax.WithBaseList(baseList);


            var asyncOperationContracts = serviceInterface.GetMembers()
                .Cast<IMethodSymbol>()
                .Where(item => ((INamedTypeSymbol)item.ReturnType).IsGenericType && item.ReturnType.Name == "Task")
                .Where(item => item.GetAttributes().Any(attr => attr.AttributeClass.Name == "OperationContractAttribute"));


            foreach (var serviceMethod in asyncOperationContracts)
            {
                var returnType = SyntaxFactory.ParseTypeName(serviceMethod.ReturnType.ToString());
                var method = SyntaxFactory.MethodDeclaration(returnType, serviceMethod.Name);

                var modif = new SyntaxTokenList();
                modif = modif.Add(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space));
                modif = modif.Add(SyntaxFactory.Token(SyntaxKind.VirtualKeyword).WithTrailingTrivia(SyntaxFactory.Space));

                method = method
                    .WithIdentifier(method.Identifier.WithLeadingTrivia(SyntaxFactory.Space))
                    .WithModifiers(modif)
                    .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

                var parameter = serviceMethod.Parameters.Single();

                var parameterType = SyntaxFactory.ParseTypeName(parameter.Type.ToString());

                var nameToken = SyntaxFactory.ParseToken(parameter.Name);

                var param = SyntaxFactory.Parameter(nameToken);
                param = param.WithIdentifier(param.Identifier.WithLeadingTrivia(SyntaxFactory.Space)).WithType(parameterType);

                method = method.AddParameterListParameters(param);

                var field = parameter.Type.GetMembers().FirstOrDefault(m => m.GetAttributes().Any(attr => attr.AttributeClass.Name == "MessageBodyMemberAttribute")) as IFieldSymbol;

                var serviceContractAttribute = serviceMethod.GetAttributes().First(attr => attr.AttributeClass.Name == "OperationContractAttribute");

                var action = serviceContractAttribute.NamedArguments.First(item => item.Key == "Action").Value.Value;
                 

                var returnTypeArg = ((INamedTypeSymbol) serviceMethod.ReturnType).TypeArguments.FirstOrDefault();

                string bodyStr = string.Empty;
                if (field != null)
                {
                    bodyStr = string.Format("return this.CallAsync<{0}, {1}>(\"{2}\", {3}.{4});", GetTypeName(field.Type), returnTypeArg.ToString(), action, parameter.Name, field.Name);    
                }
                else
                {
                    bodyStr = string.Format("return this.CallAsync<{0}, {1}>(\"{2}\", {3});", parameterType, returnTypeArg.ToString(), action, parameter.Name);    
                }
                
                var stmt = SyntaxFactory.ParseStatement(bodyStr).WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

                var block = SyntaxFactory.Block(stmt);
                block = block
                    .WithOpenBraceToken(block.OpenBraceToken.WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed))
                    .WithCloseBraceToken(block.CloseBraceToken.WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));

                method = method.WithBody(block);

                classDeclarationSyntax = classDeclarationSyntax.AddMembers(method);
            }


            return classDeclarationSyntax;
        }

		private static CodeTypeDeclaration AddClientImplementation(ServiceInterface serviceInterface)
		{
			var classTypeDec = new CodeTypeDeclaration(serviceInterface.Name + "Client");
			classTypeDec.IsClass = true;
			classTypeDec.IsPartial = true;
			classTypeDec.TypeAttributes |= TypeAttributes.Public;

			classTypeDec.BaseTypes.Add(ClientBaseClassName);
			classTypeDec.BaseTypes.Add(new CodeTypeReference(serviceInterface.Name));

			foreach (var serviceMethod in serviceInterface.Methods)
			{
				var mth = new CodeMemberMethod();
				mth.Attributes = MemberAttributes.Public;
				mth.Name = serviceMethod.Name;
				mth.ReturnType = new CodeTypeReference(serviceMethod.MethodInfo.ReturnType);

				var parameterName = "request";
				var parameterInfos = serviceMethod.MethodInfo.GetParameters();
				Type sendType = parameterInfos[0].ParameterType;

				foreach (var parameterInfo in parameterInfos)
				{
					var bodyMember =
						parameterInfo.ParameterType.GetMembers(BindingFlags.Instance | BindingFlags.Public)
							.FirstOrDefault(m => m.GetCustomAttribute<System.ServiceModel.MessageBodyMemberAttribute>() != null);

					if (bodyMember != null)
					{
						parameterName += "." + bodyMember.Name;
						var fieldInfo = bodyMember as FieldInfo;
						if (fieldInfo != null)
						{
							sendType = fieldInfo.FieldType;
						}

						var propertyInfo = bodyMember as PropertyInfo;
						if (propertyInfo != null)
						{
							sendType = propertyInfo.PropertyType;
						}
					}

					mth.Parameters.Add(new CodeParameterDeclarationExpression(parameterInfo.ParameterType, parameterInfo.Name));
				}

				var returnStatement = new CodeMethodReturnStatement();

				var invokeExpression = new CodeMethodInvokeExpression(new CodeThisReferenceExpression(), "CallAsync", new CodePrimitiveExpression(serviceMethod.Action), new CodeVariableReferenceExpression(parameterName));
				invokeExpression.Method.TypeArguments.Add(sendType);
				invokeExpression.Method.TypeArguments.Add(serviceMethod.MethodInfo.ReturnType.GenericTypeArguments[0]);
				returnStatement.Expression = invokeExpression;

				mth.Statements.Add(returnStatement);

				classTypeDec.Members.Add(mth);
			}

			return classTypeDec;
		}
        

		private static Assembly Compile(string csFilePath)
		{
			var codeDomProvider = CodeDomProvider.CreateProvider("CS");

			var compilerParameters = new CompilerParameters
			{
				GenerateExecutable = false,
				GenerateInMemory = true,
			};

			compilerParameters.ReferencedAssemblies.Add("System.dll");
			compilerParameters.ReferencedAssemblies.Add("System.Xml.dll");
			compilerParameters.ReferencedAssemblies.Add("System.ServiceModel.dll");
			compilerParameters.ReferencedAssemblies.Add("System.Runtime.Serialization.dll");

			var compilerResults = codeDomProvider.CompileAssemblyFromFile(compilerParameters, csFilePath);
			var assembly = compilerResults.CompiledAssembly;
			return assembly;
		}

		private static void GenerateRawCode(string svcutilPath, string wsdlUri, string outFile)
		{
			if (!File.Exists(svcutilPath))
				throw new Exception("svcutil is not found");

			var processStartInfo = new ProcessStartInfo(svcutilPath);
			processStartInfo.WindowStyle = ProcessWindowStyle.Hidden;

			var tempFileName = Path.GetTempFileName();
            processStartInfo.Arguments = String.Format("\"{0}\" /noconfig /nologo /t:code /mc /edb /out:\"{1}\"", wsdlUri, tempFileName);

			Process.Start(processStartInfo).WaitForExit();

			File.Copy(tempFileName + ".cs", outFile, true);
		}
	}

	class AppParameter
	{
		public string Key { get; set; }
		public string Value { get; set; }

		public AppParameter(string key, string value = null)
		{
			Key = key;
			Value = value;
		}
	}

    internal static class Ext
    {
        internal static T GetArgumentValueOrDefault<T>(this ImmutableArray<KeyValuePair<string, TypedConstant>> parameters, string paramName)
        {
            if (!parameters.Any(item => item.Key == paramName)) return default(T);

            return (T)parameters.First(item => item.Key == paramName).Value.Value;
        }
    }
}
