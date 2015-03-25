using System;
using System.CodeDom;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.ServiceModel;

namespace SoapClientGenerator.SoapClientGenerator.CodeDom
{
    public class CodeDomSoapClientGenerator : SoapClientGeneratorBase
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

            // 2. complile assembly
            Console.WriteLine("Compile...");
            var assembly = Compile(csFilePath);

            // 3. ParseAssembly
            Console.WriteLine("Gathering info...");
            var services = ParseAssembly(assembly);

            // 4. Generate source code
            Console.WriteLine("Code generation...");
            GeneratedCode(services, outFilePath, ns);

            Console.WriteLine("DONE!");
            Console.WriteLine();
        }

        private void GeneratedCode(AssemblyInfo assemblyInfo, string srcFile, string ns)
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

        private CodeTypeDeclaration AddClientImplementation(ServiceInterface serviceInterface)
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

        private CodeTypeDeclaration AddClientInterface(ServiceInterface serviceInterface)
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

        private AssemblyInfo ParseAssembly(Assembly assembly)
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

        private Assembly Compile(string csFilePath)
        {
            var codeDomProvider = CodeDomProvider.CreateProvider("CS");

            var compilerParameters = new CompilerParameters()
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
    }
}
