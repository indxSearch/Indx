using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Net.Http.Headers;
using System.Text;
namespace IndxServer
{
    /// <summary>
    /// Class to ensure proper formatting for both json and plain text.
    /// </summary>
    public class TextPlainInputFormatter : TextInputFormatter
    {
        #region Public Constructors
        /// <summary>
        /// empty ctor
        /// </summary>
        public TextPlainInputFormatter()
        {
            // supported media types
            SupportedMediaTypes.Add(MediaTypeHeaderValue.Parse("text/plain"));
            // supported encodings
            SupportedEncodings.Add(Encoding.UTF8);
            SupportedEncodings.Add(Encoding.Unicode);
        }
        #endregion Public Constructors

        #region Public Methods
        /// <summary>
        /// Required override for abstract parent class
        /// </summary>
        /// <param name="context"></param>
        /// <param name="encoding"></param>
        /// <returns></returns>
        public override async Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context, Encoding encoding)
        {
            var request = context.HttpContext.Request;
            using var reader = new StreamReader(request.Body, encoding);
            var content = await reader.ReadToEndAsync();
            return await InputFormatterResult.SuccessAsync(content);
        }
        #endregion Public Methods
    }
}