using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.ServiceModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.MSBuild;

namespace SoapClientGenerator.Roslyn
{
    public class RoslynSoapClientGenerator : SoapClientGeneratorBase
    {
        public override void GenerateSoapClient()
        {
            CreateProxy(SvcUtilPath, WsdlUri, ResultFilePath, Namespace);
        }

        private void CreateProxy(string svcutil, string wsdlUri, string outFilePath, string ns)
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

            var compilation = CSharpCompilation.Create("ServiceReference", new[] { syntaxTree }, new[] { mscorlib, serialization });
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetRoot();



            // 3. Parse trees
            Console.WriteLine("Gathering info...");
            var metadata = ParseTree(root, semanticModel);

            // 4. Generate source code
            Console.WriteLine("Code generation...");
            var newRoot = GenerateCode(metadata, ns);
            MSBuildWorkspace workspace = MSBuildWorkspace.Create();
            var res = Formatter.Format(newRoot, workspace);

            File.WriteAllText(outFilePath, res.ToFullString());


            Console.WriteLine("DONE!");
            Console.WriteLine();
        }

        private NamespaceDeclarationSyntax GenerateCode(CollectMetadataSyntaxWalker metadataInfo, string ns)
        {
            var nameSpace = SyntaxFactory
                .NamespaceDeclaration(SyntaxFactory.ParseName(ns))
                .AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Xml.Serialization")));

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

        private CollectMetadataSyntaxWalker ParseTree(SyntaxNode root, SemanticModel semanticModel)
        {
            var visitor = new CollectMetadataSyntaxWalker(semanticModel);

            visitor.Visit(root);

            return visitor;
        }

        private ClassDeclarationSyntax AddDataContract(INamedTypeSymbol classInfo)
        {
            var classDecl = SyntaxFactory
                .ClassDeclaration(classInfo.Name)
                .WithModifiers(SyntaxTokenList.Create(SyntaxFactory.Token(SyntaxKind.PublicKeyword)));

            if (classInfo.BaseType.Name != "Object")
            {
                var baseTypeList = SyntaxFactory.BaseList().AddTypes(SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(classInfo.BaseType.ToString())));
                classDecl = classDecl.WithBaseList(baseTypeList);
            }

            var xmlTypeAttr = classInfo.GetAttributes().FirstOrDefault(attr => attr.AttributeClass.Name == "XmlTypeAttribute");
            var messageContractAttr = classInfo.GetAttributes().FirstOrDefault(attr => attr.AttributeClass.Name == "MessageContractAttribute");

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

            var bodyMember = classInfo.GetMembers().FirstOrDefault(m => m.Kind == SymbolKind.Field && m.GetAttributes().Any(attr => attr.AttributeClass.Name == "MessageBodyMemberAttribute"));
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

                classDecl = classDecl.AddAttributeLists(SyntaxFactory.AttributeList().AddAttributes(attr));
            }

            classDecl = AddDataContractFields(classDecl, classInfo);
            classDecl = AddDataContractProperties(classDecl, classInfo);

            return classDecl;
        }

        private ClassDeclarationSyntax AddDataContractProperties(ClassDeclarationSyntax classDecl, INamedTypeSymbol classInfo)
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
                    .AddAccessors(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))
                    .AddAccessors(SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));

                var propertySyntax = SyntaxFactory.PropertyDeclaration(SyntaxFactory.ParseTypeName(GetTypeName(propertySymbol.Type)), name);
                propertySyntax = propertySyntax
                    .WithModifiers(SyntaxTokenList.Create(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                    .WithAccessorList(list);


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

                    propertySyntax = propertySyntax.AddAttributeLists(SyntaxFactory.AttributeList().AddAttributes(xmlElementAttrSyntax));
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

                    propertySyntax = propertySyntax.AddAttributeLists(SyntaxFactory.AttributeList().AddAttributes(xmlAnyElementAttrSyntax));
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

                    propertySyntax = propertySyntax.AddAttributeLists(SyntaxFactory.AttributeList().AddAttributes(xmlAttributeAttrSyntax));
                }

                var xmlTextAttr = propertySymbol.GetAttributes().FirstOrDefault(attr => attr.AttributeClass.Name == "XmlTextAttribute");
                if (xmlTextAttr != null)
                {
                    var xmlTextAttrSyntax = SyntaxFactory.Attribute(SyntaxFactory.ParseName("XmlTextAttribute"));

                    propertySyntax = propertySyntax.AddAttributeLists(SyntaxFactory.AttributeList().AddAttributes(xmlTextAttrSyntax));
                }

                classDecl = classDecl.AddMembers(propertySyntax);
            }

            return classDecl;
        }

        private string GetPropertyTypeName(ITypeSymbol type)
        {
            if (type.Name == "XmlElement") return "XElement";
            if (type.Name == "XmlElement[]") return "XElement[]";

            return GetTypeName(type);
        }

        private ClassDeclarationSyntax AddDataContractFields(ClassDeclarationSyntax classDecl, INamedTypeSymbol classInfo)
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
                var decl = SyntaxFactory.VariableDeclarator(fieldSymbol.Name);


                var fieldSyntax = SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(type).WithVariables(SyntaxFactory.SeparatedList(new[] { decl })));

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
                    .AddAttributeLists(SyntaxFactory.AttributeList().AddAttributes(xmlElementAttrSyntax))
                    .WithModifiers(SyntaxTokenList.Create(SyntaxFactory.Token(SyntaxKind.PublicKeyword)));

                classDecl = classDecl.AddMembers(fieldSyntax);

            }

            return classDecl;
        }

        private string GetTypeName(ITypeSymbol type)
        {
            List<string> elementar = new List<string>
            {
                "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double", "decimal", "char", "string", "bool", "object"
            };
            var name = type.ToString();
            if (elementar.Contains(name)) return name;

            return type.Name;
        }

        private string GetNamespace(AttributeData xmlElementAttr, AttributeData messageBodyMemberAttr)
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

        private string GetDataType(AttributeData xmlElementAttr)
        {
            if (xmlElementAttr == null) return null;
            return xmlElementAttr.NamedArguments.GetArgumentValueOrDefault<string>("DataType");
        }

        private int GetOrder(AttributeData xmlElementAttr, AttributeData messageBodyMemberAttr)
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

        private EnumDeclarationSyntax AddEnum(INamedTypeSymbol enumInfo)
        {
            var enumDecl = SyntaxFactory
                .EnumDeclaration(enumInfo.Name)
                .WithModifiers(SyntaxTokenList.Create(SyntaxFactory.Token(SyntaxKind.PublicKeyword)));

            foreach (var member in enumInfo.GetMembers().Where(m => m.Kind == SymbolKind.Field))
            {
                var memberSyntax = SyntaxFactory.EnumMemberDeclaration(member.Name);
                enumDecl = enumDecl.AddMembers(memberSyntax);
            }

            return enumDecl;
        }

        private InterfaceDeclarationSyntax AddClientInterface(INamedTypeSymbol serviceInterface)
        {
            var interfaceDeclarationSyntax = SyntaxFactory
                .InterfaceDeclaration(serviceInterface.Name)
                .WithModifiers(SyntaxTokenList.Create(SyntaxFactory.Token(SyntaxKind.PublicKeyword)));
            
            var asyncOperationContracts = serviceInterface.GetMembers()
                .Cast<IMethodSymbol>()
                .Where(item => ((INamedTypeSymbol)item.ReturnType).IsGenericType && item.ReturnType.Name == "Task")
                .Where(item => item.GetAttributes().Any(attr => attr.AttributeClass.Name == "OperationContractAttribute"));
            
            foreach (var serviceMethod in asyncOperationContracts)
            {
                var returnType = SyntaxFactory.ParseTypeName(serviceMethod.ReturnType.ToString());
                var method = SyntaxFactory
                    .MethodDeclaration(returnType, serviceMethod.Name)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)
                    .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));

                foreach (var parameter in serviceMethod.Parameters)
                {
                    var parameterType = SyntaxFactory.ParseTypeName(parameter.Type.ToString());

                    var nameToken = SyntaxFactory.ParseToken(parameter.Name);

                    var param = SyntaxFactory.Parameter(nameToken);
                    param = param.WithIdentifier(param.Identifier).WithType(parameterType);

                    method = method.AddParameterListParameters(param);
                }

                interfaceDeclarationSyntax = interfaceDeclarationSyntax.AddMembers(method);
            }

            return interfaceDeclarationSyntax;
        }

        private ClassDeclarationSyntax AddClientImplementation(INamedTypeSymbol serviceInterface)
        {
            var interfaceBaseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(serviceInterface.Name));
            var clientBaseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(ClientBaseClassName));
            var baseList = SyntaxFactory.BaseList().AddTypes(clientBaseType, interfaceBaseType);
            
            var classDeclarationSyntax = SyntaxFactory
                .ClassDeclaration(serviceInterface.Name + "Client")
                .WithBaseList(baseList)
                .WithModifiers(SyntaxTokenList.Create(SyntaxFactory.Token(SyntaxKind.PublicKeyword)).Add(SyntaxFactory.Token(SyntaxKind.PartialKeyword)));
            
            var asyncOperationContracts = serviceInterface.GetMembers()
                .Cast<IMethodSymbol>()
                .Where(item => ((INamedTypeSymbol)item.ReturnType).IsGenericType && item.ReturnType.Name == "Task")
                .Where(item => item.GetAttributes().Any(attr => attr.AttributeClass.Name == "OperationContractAttribute"));
            
            foreach (var serviceMethod in asyncOperationContracts)
            {
                var returnType = SyntaxFactory.ParseTypeName(serviceMethod.ReturnType.ToString());
                var method = SyntaxFactory.MethodDeclaration(returnType, serviceMethod.Name);

                var modif = new SyntaxTokenList();
                modif = modif.Add(SyntaxFactory.Token(SyntaxKind.PublicKeyword));
                modif = modif.Add(SyntaxFactory.Token(SyntaxKind.VirtualKeyword));

                method = method.WithModifiers(modif);


                var parameter = serviceMethod.Parameters.Single();

                var parameterType = SyntaxFactory.ParseTypeName(parameter.Type.ToString());

                var nameToken = SyntaxFactory.ParseToken(parameter.Name);

                var param = SyntaxFactory.Parameter(nameToken);
                param = param.WithType(parameterType);

                method = method.AddParameterListParameters(param);

                var field = parameter.Type.GetMembers().FirstOrDefault(m => m.GetAttributes().Any(attr => attr.AttributeClass.Name == "MessageBodyMemberAttribute")) as IFieldSymbol;

                var serviceContractAttribute = serviceMethod.GetAttributes().First(attr => attr.AttributeClass.Name == "OperationContractAttribute");

                var action = serviceContractAttribute.NamedArguments.First(item => item.Key == "Action").Value.Value;


                var returnTypeArg = ((INamedTypeSymbol)serviceMethod.ReturnType).TypeArguments.FirstOrDefault();

                string bodyStr = string.Empty;
                if (field != null)
                {
                    bodyStr = string.Format("return this.CallAsync<{0}, {1}>(\"{2}\", {3}.{4});", GetTypeName(field.Type), returnTypeArg.ToString(), action, parameter.Name, field.Name);
                }
                else
                {
                    bodyStr = string.Format("return this.CallAsync<{0}, {1}>(\"{2}\", {3});", parameterType, returnTypeArg.ToString(), action, parameter.Name);
                }

                var stmt = SyntaxFactory.ParseStatement(bodyStr);

                var block = SyntaxFactory.Block(stmt);

                method = method.WithBody(block);

                classDeclarationSyntax = classDeclarationSyntax.AddMembers(method);
            }


            return classDeclarationSyntax;
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
