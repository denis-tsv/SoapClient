namespace SoapClientGenerator
{
    public interface ISoapClientGenerator
    {
        string SvcUtilPath { get; set; }

        string WsdlUri { get; set; }

        string Namespace { get; set; }

        string ResultFilePath { get; set; }

        bool ImplementINotifyPropertyChanged { get; set; }

        bool GenerateDataContracts { get; set; }

        void GenerateSoapClient();
    }
}
