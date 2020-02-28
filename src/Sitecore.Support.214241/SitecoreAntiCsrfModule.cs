using Sitecore.Configuration;
using Sitecore.Diagnostics;
using Sitecore.Security.AntiCsrf;
using Sitecore.Security.AntiCsrf.Configuration;
using Sitecore.Security.AntiCsrf.Exceptions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web;
using System.Web.UI;
using System.Xml;
using System.Xml.Linq;

namespace Sitecore.Support.Security.AntiCsrf
{
    public class SitecoreAntiCsrfModule: IHttpModule
    {
        private const string ContextIndexName = "Sitecore.Security.AntiCsrf.AntiCsrfModule.CsrfToken";
        private static readonly List<AntiCsrfRule> InternalRulesList = new List<AntiCsrfRule>();
        private static readonly object InitializationLock = new object();
        private static bool isInitialized = false;
        private static readonly Dictionary<string, bool> AttributeCache = new Dictionary<string, bool>();

        // Methods
        private static void AddExclusionStateToCache(string assemblyQualifiedName, bool exclusionState)
        {
            Assert.ArgumentNotNull(assemblyQualifiedName, "assemblyQualifiedName");
            if (AttributeCache.ContainsKey(assemblyQualifiedName))
            {
                if (AttributeCache[assemblyQualifiedName] == exclusionState)
                {
                    return;
                }
                lock (AttributeCache)
                {
                    AttributeCache[assemblyQualifiedName] = exclusionState;
                    return;
                }
            }
            lock (AttributeCache)
            {
                if (!AttributeCache.ContainsKey(assemblyQualifiedName))
                {
                    AttributeCache.Add(assemblyQualifiedName, exclusionState);
                }
            }
        }

        public void Dispose()
        {
        }

        private static bool GuidTryParse(string s, out Guid result)
        {
            result = Guid.Empty;
            if (s == null)
            {
                return false;
            }
            try
            {
                result = new Guid(s);
            }
            catch (FormatException)
            {
                return false;
            }
            return true;
        }

        public void Init(HttpApplication context)
        {
            Assert.ArgumentNotNull(context, "context");
            if (Sitecore.Security.AntiCsrf.Configuration.Settings.Enabled)
            {
                lock (InitializationLock)
                {
                    if (!isInitialized)
                    {
                        this.LoadConfiguration();
                        isInitialized = true;
                    }
                }
                context.PreSendRequestHeaders += new EventHandler(this.PreSendRequestHeaders);
                context.PreRequestHandlerExecute += new EventHandler(this.PreRequestHandlerExecute);
            }
        }

        protected virtual void InitializeRuleFilters(AntiCsrfRule csrfRule, XElement rule)
        {
            if ((csrfRule != null) && (rule != null))
            {
                foreach (XElement element in rule.Elements("ignore"))
                {
                    XAttribute attribute = element.Attribute("contains");
                    if ((attribute != null) && (attribute.Value.Trim().Length > 0))
                    {
                        csrfRule.AddFilter(new AntiCsrfUrlFilter(attribute.Value));
                        continue;
                    }
                    attribute = element.Attribute("wildcard");
                    if ((attribute != null) && (attribute.Value.Trim().Length > 0))
                    {
                        csrfRule.AddFilter(new AntiCsrfWildcardUrlFilter(attribute.Value));
                    }
                }
            }
        }

        protected virtual void LoadConfiguration()
        {
            XmlNode configNode = Factory.GetConfigNode("AntiCsrf");
            if (configNode != null)
            {
                foreach (XElement element2 in XElement.Parse(configNode.OuterXml).Descendants("rule"))
                {
                    this.LoadRule(element2);
                }
            }
        }

        protected virtual void LoadRule(XElement rule)
        {
            if (rule != null)
            {
                XElement element = rule.Element("urlPrefix");
                if ((element != null) && !string.IsNullOrEmpty(element.Value))
                {
                    string urlPrefix = element.Value;
                    XAttribute attribute = rule.Attribute("name");
                    string ruleName = string.Empty;
                    if (attribute != null)
                    {
                        ruleName = attribute.Value;
                    }
                    AntiCsrfRule item = InternalRulesList.Find(r => r.UrlPrefix == urlPrefix);
                    if (item == null)
                    {
                        item = new AntiCsrfRule(urlPrefix, ruleName);
                        InternalRulesList.Add(item);
                    }
                    this.InitializeRuleFilters(item, rule);
                }
            }
        }

        private static void PageLoad(object sender, EventArgs eventArgs)
        {
            Assert.ArgumentNotNull(sender, "sender");
            Page page = (Page)sender;
            if (page.IsPostBack && page.Request.HttpMethod.Equals("GET"))
            {
                RaiseError(new BadPostBackException(), HttpContext.Current);
            }
        }

        private static void PagePreRender(object sender, EventArgs eventArgs)
        {
            Assert.ArgumentNotNull(sender, "sender");
            Assert.ArgumentNotNull(eventArgs, "eventArgs");
            Page page = sender as Page;
            if ((page != null) && (page.Form != null))
            {
                string s = string.Empty;
                HttpContext current = HttpContext.Current;
                HttpCookie cookie = current.Request.Cookies[Sitecore.Security.AntiCsrf.Configuration.Settings.CookieName];
                if ((cookie == null) || string.IsNullOrEmpty(cookie.Value))
                {
                    s = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
                    current.Items["Sitecore.Security.AntiCsrf.AntiCsrfModule.CsrfToken"] = s;
                }
                else
                {
                    Guid guid;
                    HttpCookie cookie2 = page.Request.Cookies[Sitecore.Security.AntiCsrf.Configuration.Settings.CookieName];
                    if (cookie2 != null)
                    {
                        s = cookie2.Value;
                    }
                    if (!GuidTryParse(s, out guid))
                    {
                        RaiseError(new PotentialCsrfException("The CSRF cookie does not appear to be in the correct format."), current);
                    }
                }
                string hiddenFieldInitialValue = new ObjectStateFormatter().Serialize(s);
                page.ClientScript.RegisterHiddenField(Sitecore.Security.AntiCsrf.Configuration.Settings.FormFieldName, hiddenFieldInitialValue);
                StringBuilder builder = new StringBuilder();
                builder.Append("<script type=\"text/javascript\">");
                builder.Append($"var scCSRFToken = {{ key: '{Sitecore.Security.AntiCsrf.Configuration.Settings.FormFieldName}', value: '{hiddenFieldInitialValue}' }};");
                builder.Append("</script>");
                page.ClientScript.RegisterStartupScript(typeof(SitecoreAntiCsrfModule), "Sitecoe.Csrf", builder.ToString(), false);
            }
        }

        private void PreRequestHandlerExecute(object sender, EventArgs e)
        {
            string assemblyQualifiedName;
            Assert.ArgumentNotNull(sender, "sender");
            HttpContext context = ((HttpApplication)sender).Context;
            if (context.Handler != null)
            {
                assemblyQualifiedName = context.Handler.GetType().AssemblyQualifiedName;
                if (assemblyQualifiedName == null)
                {
                    return;
                }
                bool flag = AttributeCache.ContainsKey(assemblyQualifiedName);
                if (flag && AttributeCache[assemblyQualifiedName])
                {
                    return;
                }
                Page handler = context.Handler as Page;
                if (!flag)
                {
                    if (context.Handler is ISuppressCsrfCheck)
                    {
                        goto TR_0002;
                    }
                    else if (context.Handler.GetType().GetCustomAttributes(typeof(SuppressCsrfCheckAttribute), true).Length <= 0)
                    {
                        AddExclusionStateToCache(assemblyQualifiedName, false);
                    }
                    else
                    {
                        goto TR_0002;
                    }
                }
                if (this.SkipByConfiguration(context.Request.RawUrl))
                {
                    return;
                }
                if (handler != null)
                {
                    handler.PreRender += new EventHandler(SitecoreAntiCsrfModule.PagePreRender);
                    handler.Load += new EventHandler(SitecoreAntiCsrfModule.PageLoad);
                    if (context.Request.HttpMethod.Equals("POST", StringComparison.Ordinal))
                    {
                        HttpCookie cookie = context.Request.Cookies[Sitecore.Security.AntiCsrf.Configuration.Settings.CookieName];
                        string str2 = context.Request.Form[Sitecore.Security.AntiCsrf.Configuration.Settings.FormFieldName];
                        if (string.IsNullOrEmpty(str2) && ((cookie == null) || string.IsNullOrEmpty(cookie.Value)))
                        {
                            RaiseError(new PotentialCsrfException("No CSRF cookie supplied and CSRF form field is missing."), context);
                        }
                        if ((cookie == null) || string.IsNullOrEmpty(cookie.Value))
                        {
                            RaiseError(new PotentialCsrfException("No CSRF cookie supplied."), context);
                        }
                        if (string.IsNullOrEmpty(str2))
                        {
                            RaiseError(new PotentialCsrfException("CSRF form field is missing."), context);
                        }
                        string str3 = string.Empty;
                        ObjectStateFormatter formatter = new ObjectStateFormatter();
                        try
                        {
                            str3 = formatter.Deserialize(context.Request.Form[Sitecore.Security.AntiCsrf.Configuration.Settings.FormFieldName]) as string;
                        }
                        catch
                        {
                            RaiseError(new PotentialCsrfException("CSRF parameter deserialization error. The CSRF parameter is in an invalid format."), context);
                        }
                        if ((cookie != null) && (cookie.Value != str3))
                        {
                            RaiseError(new PotentialCsrfException("The CSRF cookie value did not match the CSRF parameter value."), context);
                        }
                    }
                }
            }
            return;
        TR_0002:
            AddExclusionStateToCache(assemblyQualifiedName, true);
        }

        private void PreSendRequestHeaders(object sender, EventArgs e)
        {
            Assert.ArgumentNotNull(sender, "sender");
            Assert.ArgumentNotNull(e, "e");
            HttpContext context = ((HttpApplication)sender).Context;
            if (context.Items["Sitecore.Security.AntiCsrf.AntiCsrfModule.CsrfToken"] != null)
            {
                WriteCsrfCookie(context.Response.Cookies, context.Items["Sitecore.Security.AntiCsrf.AntiCsrfModule.CsrfToken"].ToString());
            }
        }

        private static void RaiseError(Exception ex, HttpContext context)
        {
            Assert.ArgumentNotNull(ex, "ex");
            Assert.ArgumentNotNull(context, "context");
            if ((Sitecore.Security.AntiCsrf.Configuration.Settings.DetectionResult == DetectionResult.Redirect) && string.IsNullOrEmpty(Sitecore.Security.AntiCsrf.Configuration.Settings.ErrorPage))
            {
                throw new NoErrorPageSpecifiedException();
            }
            if (Sitecore.Security.AntiCsrf.Configuration.Settings.DetectionResult != DetectionResult.Redirect)
            {
                throw ex;
            }
            context.Response.Redirect(Sitecore.Security.AntiCsrf.Configuration.Settings.ErrorPage, true);
        }

        public bool SkipByConfiguration(string rawUrl)
        {
            Assert.ArgumentNotNull(rawUrl, "rawUrl");
            bool flag = false;
            using (IEnumerator<AntiCsrfRule> enumerator = Rules.GetEnumerator())
            {
                while (true)
                {
                    if (!enumerator.MoveNext())
                    {
                        break;
                    }
                    AntiCsrfRule current = enumerator.Current;
                    flag = current.IsAntiCsrfUrl(rawUrl) || flag;
                    if (current.FilterUrl(rawUrl))
                    {
                        return true;
                    }
                }
            }
            return !flag;
        }

        private static void WriteCsrfCookie(HttpCookieCollection cookies, string value)
        {
            Assert.ArgumentNotNull(cookies, "cookies");
            Assert.ArgumentNotNull(value, "value");
            HttpCookie cookie = new HttpCookie(Sitecore.Security.AntiCsrf.Configuration.Settings.CookieName)
            {
                Value = value,
                HttpOnly = true
            };
            cookies.Add(cookie);
        }

        // Properties
        [Obsolete("Please use SitecoreAntiCsrfModule.RulesList instead")]
        public IEnumerable<AntiCsrfRule> Rules =>
            RulesList;

        public static IEnumerable<AntiCsrfRule> RulesList =>
            InternalRulesList;

    }

}
