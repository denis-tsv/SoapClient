using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace SoapClientGenerator
{
	internal class AssemblyInfo
	{
		public List<ServiceInterface> Services { get; set; }
		public List<ContractInfo> Contracts { get; set; }
		public List<EnumInfo> Enums { get; set; }
	}

    public class MetadataInfo
    {
        public List<INamedTypeSymbol> Services { get; set; }
        public List<INamedTypeSymbol> Contracts { get; set; }
        public List<INamedTypeSymbol> Enums { get; set; }
    }

}