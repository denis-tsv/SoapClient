using System;
using System.Diagnostics;
using System.IO;

namespace SoapClientGenerator
{
    public abstract class SoapClientGeneratorBase : ISoapClientGenerator
    {
        protected const string ClientBaseClassName = "SoapServices.SoapClientBase";

        public string SvcUtilPath { get; set; }

        public string WsdlUri { get; set; }

        public string Namespace { get; set; }

        public string ResultFilePath { get; set; }

        public bool ImplementINotifyPropertyChanged
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }

        public bool GenerateDataContracts
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }

        public abstract void GenerateSoapClient();

        protected void GenerateRawCode(string svcutilPath, string wsdlUri, string outFile)
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
}
