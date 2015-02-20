// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Web.Http.Controllers;
using System.Web.Http.Routing;

namespace System.Web.Http.Dispatcher
{
    /// <summary>
    /// An alternative implementation of <see cref="IHttpControllerSelector"/> that allows for a patterend Namespace versioning implementation.
    /// If the controllers are implemented in a namespace structure that is [base namespace].[version specifier].[controller],
    /// this Selector will use specified header as a date value to peform effective date base logic to select the appropriate controller
    /// by when the namespace version combination became effective.  The underlying controllers are either replicated to support future versions
    /// or use an inheritance model to add, or by using the new keyword (hiding) an action.
    /// 
    /// <example>
    ///     v1 launched on 2015-01-01 with ValuesController (namespace Contoso.Controllers.v1)
    ///     v2 launched on 2015-03-01 with ValuesController (namespace Contoso.Controllers.v2)
    ///     v3 laucnhed on 2016-05-01 with ValuesController (namespace Contoso.Controllers.v3)
    /// 
    ///     The header is x-version sent by a client:
    ///         
    ///         value = 2014-01-01, a 404 is returend because there isnt a version with ValuesController that was effective
    ///         value = 2015-01-01, Contoso.Controllers.v1 is selected
    ///         value = 2015-01-02, Contoso.Controllers.v1 is selected
    ///         value = 2015-03-01, Contoso.Controllers.v2 is selected
    ///         value = 2015-05-01, Contoso.Controllers.v3 is selected
    ///         value = 2015-06-01, Contoso.Controllers.v1 is selected
    /// 
    /// </example>
    /// 
    /// This was adpated from Mark Wasson's blog example (http://blogs.msdn.com/b/webdev/archive/2013/03/08/using-namespaces-to-version-web-apis.aspx)
    /// 
    /// </summary>
    public class NamespaceHttpControllerSelector : IHttpControllerSelector
    {
        private readonly string _versionHeaderKey;
        private readonly HttpConfiguration _configuration;
        private readonly Lazy<Dictionary<string, HttpControllerDescriptor>> _controllers;
        private readonly LinkedList<KeyValuePair<DateTime, string>> _versions;

        /// <summary>
        /// Initializes a new instance of the <see cref="NamespaceHttpControllerSelector"/> class.
        /// </summary>
        /// <param name="configuration">The configuration.</param>
        /// <param name="versions">Array of Effective Dates and version specifiers.  Version specifier must match the last part of the namespace for controllers</param>
        /// <param name="versionHeaderName">The customer HTTP header in W3C format (x-param-name) to look for client version specification.  If a client omits 
        /// the value, the latest is assumed.</param>
        public NamespaceHttpControllerSelector(HttpConfiguration configuration, KeyValuePair<DateTime, string>[] versions, string versionHeaderName)
        {
            _configuration = configuration;
            _versionHeaderKey = versionHeaderName;
            _controllers = new Lazy<Dictionary<string, HttpControllerDescriptor>>(InitializeControllerDictionary);
            _versions = new LinkedList<KeyValuePair<DateTime, string>>(versions.OrderBy(ver => ver.Key));
        }

        private Dictionary<string, HttpControllerDescriptor> InitializeControllerDictionary()
        {
            var dictionary = new Dictionary<string, HttpControllerDescriptor>(StringComparer.OrdinalIgnoreCase);
            var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Create a lookup table where key is "namespace.controller". The value of "namespace" is the last
            // segment of the full namespace. For example:
            // MyApplication.Controllers.V1.ProductsController => "V1.Products"
            IAssembliesResolver assembliesResolver = _configuration.Services.GetAssembliesResolver();
            IHttpControllerTypeResolver controllersResolver = _configuration.Services.GetHttpControllerTypeResolver();
            ICollection<Type> controllerTypes = controllersResolver.GetControllerTypes(assembliesResolver);

            foreach (Type types in controllerTypes)
            {
                var segments = types.Namespace.Split(Type.Delimiter);

                // For the dictionary key, strip "Controller" from the end of the type name.
                // This matches the behavior of DefaultHttpControllerSelector.
                var controllerName = types.Name.Remove(types.Name.Length - DefaultHttpControllerSelector.ControllerSuffix.Length);
                var key = String.Format(CultureInfo.InvariantCulture, "{0}.{1}", segments[segments.Length - 1], controllerName);

                // Check for duplicate keys.
                if (dictionary.Keys.Contains(key))
                {
                    duplicates.Add(key);
                }
                else
                {
                    dictionary[key] = new HttpControllerDescriptor(_configuration, types.Name, types);
                }
            }

            // Remove any duplicates from the dictionary, because these create ambiguous matches. 
            // For example, "Foo.V1.ProductsController" and "Bar.V1.ProductsController" both map to "v1.products".
            foreach (string s in duplicates)
            {
                dictionary.Remove(s);
            }
            return dictionary;
        }

        public IDictionary<string, HttpControllerDescriptor> GetControllerMapping()
        {
            return _controllers.Value;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "The HttpResponseMessage that is tagged as disposable is managing the lifecycle of a dependent resource (Content) which is null in this case and doesn't need to be disposed")]
        private LinkedListNode<KeyValuePair<DateTime, string>> FindStartingVersion(DateTime versionDate)
        {
            var current = _versions.Last;

            while (current != null)
            {
                if (versionDate >= current.Value.Key)
                {
                    return current;
                }

                current = current.Previous;
            }

            throw new HttpResponseException(new HttpResponseMessage(HttpStatusCode.NotFound) { ReasonPhrase = "Version is not supported" });
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "The HttpResponseMessage that is tagged as disposable is managing the lifecycle of a dependent resource (Content) which is null in this case and doesn't need to be disposed")]
        public HttpControllerDescriptor SelectController(HttpRequestMessage request)
        {
            IHttpRouteData routeData = request.GetRouteData();

            if (routeData == null)
            {
                throw new HttpResponseException(HttpStatusCode.NotFound);
            }

            LinkedListNode<KeyValuePair<DateTime, string>> currentVersion = _versions.Last;

            IEnumerable<string> headerValues = null;
            if (request.Headers.TryGetValues(_versionHeaderKey, out headerValues))
            {
                DateTime versionSpecified = DateTime.MinValue;

                if (DateTime.TryParse(headerValues.First(), out versionSpecified))
                {
                    currentVersion = FindStartingVersion(versionSpecified);
                }
            }

            CandidateAction[] listOfCandidate = routeData.GetDirectRouteCandidates();

            if (listOfCandidate != null && listOfCandidate.Any())
            {
                while (currentVersion != null)
                {
                    foreach (var candidate in listOfCandidate)
                    {
                        var controller = candidate.ActionDescriptor.ControllerDescriptor;
                        if (controller.ControllerType.Namespace.EndsWith("." + currentVersion.Value.Value, StringComparison.OrdinalIgnoreCase))
                        {
                            return controller;
                        }
                    }
                    currentVersion = currentVersion.Previous;
                }
            }

            throw new HttpResponseException(new HttpResponseMessage(HttpStatusCode.NotFound) { ReasonPhrase = "Version is not supported" });
        }
    }
}
