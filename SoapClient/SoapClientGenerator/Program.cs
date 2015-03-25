using System;
using System.Collections.Generic;
using System.Linq;
using SoapClientGenerator.Roslyn;
using SoapClientGenerator.SoapClientGenerator.CodeDom;

namespace SoapClientGenerator
{
    class Program
	{
		private const string SvcUtilDefaultPath = @"C:\Program Files (x86)\Microsoft SDKs\Windows\v8.1A\bin\NETFX 4.5.1 Tools\SvcUtil.exe";
        private const int HelpHeaderWidth = 30;

		static void Main(string[] args)
		{
			try
			{
				//Debugger.Launch();
				WriteInfo();

				if (args.Length < 3 || args.Length > 4)
				{
					Console.WriteLine("Wrong number of parameters ({0}), expected 3 or 4", args.Length);
					return;
				}

                ISoapClientGenerator generator = new RoslynSoapClientGenerator(); 
                //ISoapClientGenerator generator = new CodeDomSoapClientGenerator();

                generator.SvcUtilPath = SvcUtilDefaultPath;
                generator.WsdlUri = args[0];
                generator.ResultFilePath = args[1];
                generator.Namespace = args[2];

				var param = GetParameters(args.Skip(3));

                if (param.ContainsKey("svcutil"))
                {
                    generator.SvcUtilPath = param["svcutil"];
                }

                generator.GenerateSoapClient();
			}
			catch (Exception e)
			{
				Console.WriteLine("ERROR: {0}", e.Message);
			}
		}

		private static Dictionary<string, string> GetParameters(IEnumerable<string> args)
		{
			var result = new Dictionary<string, string>();

			foreach (var s in args)
			{
				if (!s.StartsWith("/"))
					throw new Exception(String.Format("Bad parameter '{0}'", s));

				var pos = s.IndexOf(':');
				if (pos == -1)
				{
					result.Add(s.Substring(1), null);
				}
				else
				{
					var key = s.Substring(1, pos - 1);
					var value = s.Substring(pos + 1);
					result.Add(key, value);
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
	}
}
