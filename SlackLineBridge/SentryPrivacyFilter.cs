using System;
using Sentry;

namespace SlackLineBridge
{
    internal static class SentryPrivacyFilter
    {
        public static SentryEvent RemoveRequestMetadata(SentryEvent sentryEvent)
        {
            // Request.Url can contain the signed proxy token in its path.
            // The SDK may recreate an empty request on access; its serializer omits that empty object.
            sentryEvent.Request = null;
            return sentryEvent;
        }

        public static Breadcrumb DropHttpBreadcrumb(Breadcrumb breadcrumb)
        {
            return string.Equals(breadcrumb.Type, "http", StringComparison.OrdinalIgnoreCase)
                ? null
                : breadcrumb;
        }
    }
}
