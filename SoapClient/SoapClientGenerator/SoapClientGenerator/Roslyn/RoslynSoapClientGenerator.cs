using System;
using System.IO;
using System.Linq;
using System.ServiceModel;
using System.Xml.Schema;
using System.Xml.Serialization;
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
			var serializer = MetadataReference.CreateFromAssembly(typeof(XmlEnumAttribute).Assembly);
			

			var compilation = CSharpCompilation.Create("ServiceReference", new[] { syntaxTree }, new[] { mscorlib, serialization, serializer });
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
	        var nameSpace = SyntaxTreeExtensions.NamespaceDeclaration(ns) //SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(ns)) //+-
		        .AddUsings("System.Xml.Serialization")
				.AddUsings("System.Xml.Linq")
				.AddUsings("System.Xml.Schema")
				.AddUsings("System");
			//.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Xml.Serialization")));

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
                .WithModifiers(SyntaxKind.PublicKeyword);

            if (classInfo.BaseType.Name != "Object")
            {
                //var baseTypeList = SyntaxFactory.BaseList().AddTypes(SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(classInfo.BaseType.ToString())));
                classDecl = classDecl.WithBaseList(classInfo.BaseType.ToString());
            }

			foreach (var customAttribute in classInfo.GetAttributes("XmlIncludeAttribute")) 
			{
				var attr = SyntaxTreeExtensions.Attribute("XmlIncludeAttribute")
					.AddArgument(string.Format("typeof({0})", customAttribute.ConstructorArguments.First().Value));

				classDecl = classDecl.AddAttribute(attr);
			}

			var xmlTypeAttr = classInfo.GetAttribute("XmlTypeAttribute"); //classInfo.GetAttributes().FirstOrDefault(attr => attr.AttributeClass.Name == "XmlTypeAttribute");
	        var messageContractAttr = classInfo.GetAttribute("MessageContractAttribute"); //.GetAttributes().FirstOrDefault(attr => attr.AttributeClass.Name == "MessageContractAttribute");

			bool addXmlRoot = false;
            string xmlRootElementName = null;
            string xmlRootNamespace = null;

            if (xmlTypeAttr != null)
            {
                addXmlRoot = true;
	            xmlRootNamespace = xmlTypeAttr.GetNamedArgument("Namespace").GetValueOrDefault<string>();
            }
            else
            {
                if (messageContractAttr != null)
                {
                    addXmlRoot = true;
	                xmlRootElementName = messageContractAttr.GetNamedArgument("WrapperName").GetValueOrDefault<string>();//.NamedArguments.First(item => item.Key == "WrapperName").Value.Value as string;
	                xmlRootNamespace = messageContractAttr.GetNamedArgument("WrapperNamespace").GetValueOrDefault<string>(); //.NamedArguments.First(item => item.Key == "WrapperNamespace").Value.Value as string;
                }
            }

            var bodyMember = classInfo.GetFields().FirstOrDefault(m => m.GetAttribute("MessageBodyMemberAttribute") != null);
            if (bodyMember != null)
            {
                xmlRootElementName = bodyMember.Name;
                var bodyAttr = bodyMember.GetAttribute("MessageBodyMemberAttribute");
	            xmlRootNamespace = bodyAttr.GetNamedArgument("Namespace").GetValueOrDefault<string>();
            }

            if (addXmlRoot)
            {
	            var attr = SyntaxTreeExtensions.Attribute("XmlRootAttribute") 
		            .AddQuotedArgument("ElementName", xmlRootElementName)
		            .AddQuotedArgument("Namespace", xmlRootNamespace);

                //classDecl = classDecl.AddAttributeLists(SyntaxFactory.AttributeList().AddAttributes(attr));//+-
	            classDecl = classDecl.AddAttribute(attr);
            }

            classDecl = AddDataContractFields(classDecl, classInfo);
            classDecl = AddDataContractProperties(classDecl, classInfo);

            return classDecl;
        }

        private ClassDeclarationSyntax AddDataContractProperties(ClassDeclarationSyntax classDecl, INamedTypeSymbol classInfo)
        {
	        foreach (var propertySymbol in classInfo.GetProperties()
				.Where(prop => prop.GetAttribute("XmlAnyAttributeAttribute") == null && prop.GetAttribute("XmlIgnoreAttribute") == null))
            {
                if (propertySymbol.Type.Name == "XmlNode" || propertySymbol.Type.Name == "ExtensionDataObject")
                    continue;
				
				var name = propertySymbol.Name == "fixed" ? "@fixed" : propertySymbol.Name;
                var list = SyntaxFactory.AccessorList()
                    .AddAccessors(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))
                    .AddAccessors(SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));

                var propertySyntax = SyntaxFactory.PropertyDeclaration(GetPropertyType(propertySymbol.Type), name)
                    .WithModifiers(SyntaxKind.PublicKeyword)
                    .WithAccessorList(list);


                var xmlElementAttrs = propertySymbol.GetAttributes("XmlElementAttribute");
                foreach (var xmlElementAttr in xmlElementAttrs)
                {
                    var xmlElementAttrSyntax = SyntaxTreeExtensions.Attribute("XmlElementAttribute");

                    var elementName = xmlElementAttr.GetNamedArgument("ElementName").GetValueOrDefault<string>();
                    if (!string.IsNullOrEmpty(elementName))
                    {
	                    xmlElementAttrSyntax = xmlElementAttrSyntax.AddQuotedArgument("ElementName", elementName);
                    }
	                if (xmlElementAttr.ConstructorArguments.Any())
	                {
		                elementName = xmlElementAttr.ConstructorArguments.First().Value as string;
                        xmlElementAttrSyntax = xmlElementAttrSyntax.AddQuotedArgument("ElementName", elementName);
					}

                    string ns = GetNamespace(xmlElementAttr, null);
                    if (ns != null)
                    {
                        xmlElementAttrSyntax = xmlElementAttrSyntax.AddQuotedArgument("Namespace", ns);
                    }

                    string dataType = GetDataType(xmlElementAttr);
                    if (dataType != null)
                    {
                        xmlElementAttrSyntax = xmlElementAttrSyntax.AddQuotedArgument("DataType", dataType);
                    }

                    int order = GetOrder(xmlElementAttr, null);
                    xmlElementAttrSyntax = xmlElementAttrSyntax.AddArgument("Order", order);

					//propertySyntax = propertySyntax.AddAttributeLists(SyntaxFactory.AttributeList().AddAttributes(xmlElementAttrSyntax)); //+-
					propertySyntax = propertySyntax.AddAttribute(xmlElementAttrSyntax); //+-
				}

                var xmlAnyElementAttr = propertySymbol.GetAttribute("XmlAnyElementAttribute");
                if (xmlAnyElementAttr != null)
                {
                    var xmlAnyElementAttrSyntax = SyntaxTreeExtensions.Attribute("XmlAnyElementAttribute");

                    var ns = xmlAnyElementAttr.GetNamedArgument("Namespace").GetValueOrDefault<string>();
                    if (ns != null)
                    {
                        xmlAnyElementAttrSyntax = xmlAnyElementAttrSyntax.AddQuotedArgument("Namespace", ns);
                    }

                    var order = xmlAnyElementAttr.GetNamedArgument("Order").GetValueOrDefault<int>();
                    xmlAnyElementAttrSyntax = xmlAnyElementAttrSyntax.AddArgument("Order", order);

                    propertySyntax = propertySyntax.AddAttribute(xmlAnyElementAttrSyntax);
                }

                var xmlAttributeAttr = propertySymbol.GetAttribute("XmlAttributeAttribute");
                if (xmlAttributeAttr != null)
                {
                    var xmlAttributeAttrSyntax = SyntaxTreeExtensions.Attribute("XmlAttributeAttribute");

                    var ns = xmlAttributeAttr.GetNamedArgument("Namespace").GetValueOrDefault<string>();
                    if (ns != null)
                    {
                        xmlAttributeAttrSyntax = xmlAttributeAttrSyntax.AddQuotedArgument("Namespace", ns);
                    }

                    var dataType = xmlAttributeAttr.GetNamedArgument("DataType").GetValueOrDefault<string>();
                    if (!string.IsNullOrEmpty(dataType))
                    {
                        xmlAttributeAttrSyntax = xmlAttributeAttrSyntax.AddQuotedArgument("DataType", dataType);
                    }

                    var form = xmlAttributeAttr.GetNamedArgument("Form").GetValueOrDefault<int>();
                    if (form != (int)XmlSchemaForm.None)
                    {
						var expression = SyntaxFactory.ParseExpression(string.Format("{0} = XmlSchemaForm.{1}", "Form", Enum.GetName(typeof(XmlSchemaForm), form)));
                        var arg = SyntaxFactory.AttributeArgument(expression);
                        xmlAttributeAttrSyntax = xmlAttributeAttrSyntax.AddArgumentListArguments(arg);
                    }

                    propertySyntax = propertySyntax.AddAttribute(xmlAttributeAttrSyntax);
                }

                var xmlTextAttr = propertySymbol.GetAttribute("XmlTextAttribute");
                if (xmlTextAttr != null)
                {
                    var xmlTextAttrSyntax = SyntaxTreeExtensions.Attribute("XmlTextAttribute");

                    propertySyntax = propertySyntax.AddAttribute(xmlTextAttrSyntax);
                }

                classDecl = classDecl.AddMembers(propertySyntax);
            }

            return classDecl;
        }

        private TypeSyntax GetPropertyType(ITypeSymbol type)
        {
	        if (type.TypeKind == TypeKind.Array)
	        {
		        var arrayType = (IArrayTypeSymbol) type;
		        var elementName = arrayType.ElementType.Name == "XmlElement" ? "XElement" : GetTypeName(arrayType.ElementType);
		        var elementType = SyntaxFactory.ParseTypeName(elementName);
				return SyntaxFactory.ArrayType(elementType).AddRankSpecifiers(SyntaxFactory.ArrayRankSpecifier());
			}
	        var typeName = type.Name == "XmlElement" ? "XElement" : GetTypeName(type);
	        return SyntaxFactory.ParseTypeName(typeName);
        }

        private ClassDeclarationSyntax AddDataContractFields(ClassDeclarationSyntax classDecl, INamedTypeSymbol classInfo)
        {
			foreach (var fieldSymbol in classInfo.GetFields())
            {
                var messageBodyMemberAttr = fieldSymbol.GetAttribute("MessageBodyMemberAttribute");
                if (messageBodyMemberAttr == null) continue;

	            TypeSyntax type;
                if (fieldSymbol.GetAttribute("XmlAnyElementAttribute") == null)
                {
	                if (fieldSymbol.Type.TypeKind == TypeKind.Array)
	                {
						type = SyntaxFactory.ParseTypeName(GetTypeName(((IArrayTypeSymbol)fieldSymbol.Type).ElementType));
		                type = SyntaxFactory.ArrayType(type).AddRankSpecifiers(SyntaxFactory.ArrayRankSpecifier());
	                }
	                else
	                {
						type = SyntaxFactory.ParseTypeName(GetTypeName(fieldSymbol.Type));
					}
				}
                else
                {
                    type = SyntaxFactory.ParseTypeName("XElement");
	                type = SyntaxFactory.ArrayType(type).AddRankSpecifiers(SyntaxFactory.ArrayRankSpecifier());
                }
				
	            FieldDeclarationSyntax fieldSyntax = SyntaxTreeExtensions.FieldDeclaration(type, fieldSymbol.Name);
				//var decl = SyntaxFactory.VariableDeclarator(fieldSymbol.Name);
				//var fieldSyntax = SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(type).WithVariables(SyntaxFactory.SeparatedList(new[] { decl })));

				var xmlElementAttr = fieldSymbol.GetAttribute("XmlElementAttribute");

                string elementName = null;
                bool isNullable = true;
                if (xmlElementAttr != null)
                {
                    var elementNameValue = xmlElementAttr.GetNamedArgument("ElementName").GetValueOrDefault<string>();
                    if (string.IsNullOrEmpty(elementNameValue))
                    {
                        elementName = elementNameValue;
                    }
	                if (xmlElementAttr.ConstructorArguments.Any())
	                {
		                elementName = xmlElementAttr.ConstructorArguments.First().Value as string;
	                }
                }

                var xmlArrayItemAttribute = fieldSymbol.GetAttribute("XmlArrayItemAttribute");
                if (xmlArrayItemAttribute != null)
                {
	                if (xmlArrayItemAttribute.ConstructorArguments.Any())
	                {
		                elementName = xmlArrayItemAttribute.ConstructorArguments.First().Value as string;
	                }
	                var elementNameValue = xmlArrayItemAttribute.GetNamedArgument("ElementName").GetValueOrDefault<string>();
	                if (!string.IsNullOrEmpty(elementNameValue))
	                {
		                elementName = elementNameValue;
	                }
	                isNullable = xmlArrayItemAttribute.GetNamedArgument("IsNullable").GetValueOrDefault<bool>();
                }

	            var xmlElementAttrSyntax = SyntaxTreeExtensions.Attribute("XmlElementAttribute")
		            .AddQuotedArgument("ElementName", elementName)
		            .AddArgument("IsNullable", isNullable.ToString().ToLower());
                
                string ns = GetNamespace(xmlElementAttr, messageBodyMemberAttr);
                if (ns != null)
                {
	                xmlElementAttrSyntax = xmlElementAttrSyntax.AddQuotedArgument("Namespace", ns);
                }

                string dataType = GetDataType(xmlElementAttr);
                if (dataType != null)
                {
					xmlElementAttrSyntax = xmlElementAttrSyntax.AddQuotedArgument("DataType", dataType);
                }

                int order = GetOrder(xmlElementAttr, messageBodyMemberAttr);
				xmlElementAttrSyntax = xmlElementAttrSyntax.AddArgument("Order", order);

				fieldSyntax = fieldSyntax
                    .AddAttribute(xmlElementAttrSyntax)
                    .WithModifiers(SyntaxKind.PublicKeyword); //WithModifiers(SyntaxTokenList.Create(SyntaxFactory.Token(SyntaxKind.PublicKeyword)));

				classDecl = classDecl.AddMembers(fieldSyntax);

            }

            return classDecl;
        }

        private string GetTypeName(ITypeSymbol type)
        {
	        return type.IsPrimitive() ? type.ToString() : type.Name;
        }

        private string GetNamespace(AttributeData xmlElementAttr, AttributeData messageBodyMemberAttr)
        {
            if (xmlElementAttr != null)
            {
                var ns = xmlElementAttr.GetNamedArgument("Namespace").GetValueOrDefault<string>();
                if (ns != null) return ns;
            }

            if (messageBodyMemberAttr != null)
            {
                var ns = messageBodyMemberAttr.GetNamedArgument("Namespace").GetValueOrDefault<string>();
                if (ns != null) return ns;
            }

            return null;
        }

        private string GetDataType(AttributeData xmlElementAttr)
        {
            if (xmlElementAttr == null) return null;
            return xmlElementAttr.GetNamedArgument("DataType").GetValueOrDefault<string>();
        }

        private int GetOrder(AttributeData xmlElementAttr, AttributeData messageBodyMemberAttr)
        {
            if (xmlElementAttr != null)
            {
                var order = xmlElementAttr.GetNamedArgument("Order").GetValueOrDefault<int>();
                if (order > 0) return order;
            }
            if (messageBodyMemberAttr != null)
            {
                var order = messageBodyMemberAttr.GetNamedArgument("Order").GetValueOrDefault<int>();
                if (order > 0) return order;
            }

            return 0;
        }

        private EnumDeclarationSyntax AddEnum(INamedTypeSymbol enumInfo)
        {
            var enumDecl = SyntaxFactory
                .EnumDeclaration(enumInfo.Name)
                .WithModifiers(SyntaxKind.PublicKeyword);

            foreach (var member in enumInfo.GetFields())
            {
				var memberSyntax = SyntaxFactory.EnumMemberDeclaration(member.Name);

	            var xmlEnumAttr = member.GetAttribute("XmlEnumAttribute");
				if (xmlEnumAttr != null)
				{
					if (xmlEnumAttr.ConstructorArguments.Any())
					{
						var attribute = SyntaxTreeExtensions.Attribute("XmlEnumAttribute")
							.AddQuotedArgument(xmlEnumAttr.ConstructorArguments.First().Value);
						memberSyntax = memberSyntax.AddAttribute(attribute);
					}
				}
				
                enumDecl = enumDecl.AddMembers(memberSyntax);
            }

            return enumDecl;
        }

        private InterfaceDeclarationSyntax AddClientInterface(INamedTypeSymbol serviceInterface)
        {
            var interfaceDeclarationSyntax = SyntaxFactory
                .InterfaceDeclaration(serviceInterface.Name)
                .WithModifiers(SyntaxKind.PublicKeyword);
            
            var asyncOperationContracts = serviceInterface.GetMethods()
                .Where(item => ((INamedTypeSymbol)item.ReturnType).IsGenericType && item.ReturnType.Name == "Task")
                .Where(item => item.GetAttribute("OperationContractAttribute") != null);
            
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
            var classDeclarationSyntax = SyntaxFactory
                .ClassDeclaration(serviceInterface.Name + "Client")
                .WithBaseList(ClientBaseClassName, serviceInterface.Name)
                .WithModifiers(SyntaxKind.PublicKeyword, SyntaxKind.PartialKeyword);
            
            var asyncOperationContracts = serviceInterface
                .GetMethods()
                .Where(item => ((INamedTypeSymbol)item.ReturnType).IsGenericType && item.ReturnType.Name == "Task")
                .Where(item => item.GetAttribute("OperationContractAttribute") != null);
            
            foreach (var serviceMethod in asyncOperationContracts)
            {
                var returnType = SyntaxFactory.ParseTypeName(serviceMethod.ReturnType.ToString());
                var method = SyntaxFactory.MethodDeclaration(returnType, serviceMethod.Name)
					.WithModifiers(SyntaxKind.PublicKeyword, SyntaxKind.VirtualKeyword);
				
                var parameter = serviceMethod.Parameters.Single();

                var parameterType = SyntaxFactory.ParseTypeName(parameter.Type.ToString());

                var nameToken = SyntaxFactory.ParseToken(parameter.Name);

                var param = SyntaxFactory.Parameter(nameToken);
                param = param.WithType(parameterType);

                method = method.AddParameterListParameters(param);

                var field = parameter.Type.GetFields().FirstOrDefault(m => m.GetAttribute("MessageBodyMemberAttribute") != null);

                var serviceContractAttribute = serviceMethod.GetAttribute("OperationContractAttribute");

                var action = serviceContractAttribute.GetNamedArgument("Action").GetValueOrDefault<string>();


                var returnTypeArg = ((INamedTypeSymbol)serviceMethod.ReturnType).TypeArguments.FirstOrDefault();

                string bodyStr = string.Empty;
                if (field != null)
                {
                    bodyStr = string.Format("return this.CallAsync<{0}, {1}>(\"{2}\", {3}.{4});", GetPropertyType(field.Type), returnTypeArg.ToString(), action, parameter.Name, field.Name);
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
    
}
