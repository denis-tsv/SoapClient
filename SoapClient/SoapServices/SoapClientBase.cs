using System;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Web.Http;

namespace SoapServices
{
	public abstract class SoapClientBase
	{
		private readonly Lazy<HttpClient> _lazyClient;

		public Uri EndpointAddress { get; set; }

		protected SoapClientBase()
		{
			_lazyClient = new Lazy<HttpClient>(CreateHttpClient);
		}

		public Func<HttpClient> CustomClientInitFunc { get; set; }

		protected virtual HttpClient CreateHttpClient()
		{
			if (EndpointAddress == null)
				throw new Exception("EndpointAddress is not set");

			if (CustomClientInitFunc != null)
				return CustomClientInitFunc();

			return new HttpClient();
		}

		protected HttpClient Client { get { return _lazyClient.Value; } }

		public async Task<TResponse> CallAsync<TRequest, TResponse>(string action, TRequest request)
		{
			IHttpContent httpContent = GetHttpContent(action, request);
			var response = await Client.PostAsync(EndpointAddress, httpContent);
			var responseContent = await response.Content.ReadAsStringAsync();
			return GetResponse<TResponse>(responseContent);
		}

		private TResponse GetResponse<TResponse>(string responseContent)
		{
			var doc = XDocument.Parse(responseContent);
			var responseMessage = new ResponseMessage(doc);
			return responseMessage.GetContent<TResponse>();
		}

		private IHttpContent GetHttpContent<TRequest>(string action, TRequest request)
		{
			return new SoapMessageContent
			{
				Action = action,
				BodyContent = request
			};
		}
	}
}