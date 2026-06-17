namespace ChatOps.Data
{
    public static class AppCategories
    {
        public static readonly Dictionary<string, (string Url, string ServiceType, bool IsReleased)> AppServices = new () {};
        //key = appservice, value = url, service, IsReleased
        
    }
}