using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SoapClientGenerator
{
    public class CollectMetadataSyntaxWalker : CSharpSyntaxWalker
    {
        private readonly SemanticModel _semanticModel;

        private readonly List<INamedTypeSymbol> _services = new List<INamedTypeSymbol>();
        private readonly List<INamedTypeSymbol> _contracts = new List<INamedTypeSymbol>();
        private readonly List<INamedTypeSymbol> _enums = new List<INamedTypeSymbol>();

        public CollectMetadataSyntaxWalker(SemanticModel semanticModel)
        {
            _semanticModel = semanticModel;
        }

        public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
        {
            _enums.Add(_semanticModel.GetDeclaredSymbol(node));
        }

        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        {
            var symbol = _semanticModel.GetDeclaredSymbol(node);
            if (symbol.GetAttributes().Any(attr => attr.AttributeClass.Name == "ServiceContractAttribute"))
            {
                _services.Add(symbol);
            }
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            var symbol = _semanticModel.GetDeclaredSymbol(node);
            if (symbol.GetAttributes().Any(attr =>attr.AttributeClass.Name == "DataContractAttribute" || attr.AttributeClass.Name == "MessageContractAttribute"))
            {
                _contracts.Add(symbol);
            }
        }

        public MetadataInfo GetCollectedMetadata()
        {
            return new MetadataInfo
            {
                Contracts = _contracts,
                Enums = _enums,
                Services = _services,
            };
        }
    }
}
