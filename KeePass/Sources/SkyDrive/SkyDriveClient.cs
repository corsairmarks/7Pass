using System;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using RestSharp;

namespace KeePass.Sources.SkyDrive
{
    internal class SkyDriveClient
    {
        private readonly RestClient _client;
        private readonly string _token;

        public SkyDriveClient(string token)
        {
            if (token == null)
                throw new ArgumentNullException("token");

            _token = token;
            _client = new RestClient
            {
                UserAgent = "7Pass",
                BaseUrl = "https://apis.live.net/v5.0/",
            };

            _client.AddDefaultParameter("token",
                token, ParameterType.UrlSegment);
        }

        public void Download(string path,
            Action<MetaListItemInfo, string, byte[]> complete)
        {
            byte[] bytes = null;
            MetaListItemInfo item = null;

            var waitInfo = new ManualResetEvent(false);
            var waitContent = new ManualResetEvent(false);

            _client.ExecuteAsync(
                Request(path + "/content"), x =>
                {
                    bytes = x.RawBytes;
                    waitContent.Set();
                });

            _client.ExecuteAsync(
                Request(path), x =>
                {
                    try
                    {
                        var root = JsonConvert
                            .DeserializeXNode(x.Content, "root")
                            .Root;

                        if (root == null)
                            return;

                        item = new MetaListItemInfo(root);
                    }
                    finally
                    {
                        waitInfo.Set();
                    }
                });

            ThreadPool.QueueUserWorkItem(_ =>
            {
                waitInfo.WaitOne();
                waitContent.WaitOne();

                var id = GetSyncPath(item.Path);
                complete(item, id, bytes);
            });
        }

        public void GetEmail(Action<string> complete)
        {
            _client.ExecuteAsync(Request("me"), x =>
            {
                var doc = JsonConvert.DeserializeXNode(
                    x.Content, "root");

                var email = doc.Root.GetValue(
                    "emails", "preferred");

                complete(email);
            });
        }

        public void GetFileMeta(string path,
            Action<MetaListItemInfo> complete)
        {
            _client.ExecuteAsync(Request(path), x =>
            {
                var root = JsonConvert
                    .DeserializeXNode(x.Content, "root")
                    .Root;

                if (root == null)
                {
                    complete(null);
                    return;
                }

                complete(new MetaListItemInfo(root));
            });
        }

        public void List(string path, Action<MetaListItemInfo,
            MetaListItemInfo[]> complete)
        {
            if (string.IsNullOrEmpty(path))
                path = "me/skydrive";

            MetaListItemInfo parent = null;
            var items = new MetaListItemInfo[0];

            var waitItems = new ManualResetEvent(false);
            var waitParent = new ManualResetEvent(false);

            _client.ExecuteAsync(Request(path + "/files"),
                x =>
                {
                    try
                    {
                        var root = JsonConvert
                            .DeserializeXNode(x.Content, "root")
                            .Root;

                        if (root == null)
                            return;

                        items = root
                            .Elements("data")
                            .Select(e => new MetaListItemInfo(e))
                            .ToArray();
                    }
                    finally
                    {
                        waitItems.Set();
                    }
                });

            _client.ExecuteAsync(Request(path), x =>
            {
                try
                {
                    var root = JsonConvert
                        .DeserializeXNode(x.Content, "root")
                        .Root;

                    if (root == null)
                        return;

                    parent = new MetaListItemInfo(root);
                }
                finally
                {
                    waitParent.Set();
                }
            });

            ThreadPool.QueueUserWorkItem(_ =>
            {
                waitItems.WaitOne();
                waitParent.WaitOne();
                complete(parent, items);
            });
        }

        public static SkyDriveClient ParsePath(
            string url, out string id)
        {
            var parts = url.Split(
                new[] {Environment.NewLine},
                StringSplitOptions.None);

            id = parts[0];
            return new SkyDriveClient(parts[1]);
        }

        public void Upload(string folder,
            string name, byte[] content,
            Action<string> completed)
        {
            var safeName = name;
            if (!name.EndsWith(".doc", StringComparison
                .InvariantCultureIgnoreCase))
            {
                safeName += ".doc";
            }

            var request = Request("{folder}/files/");
            request.Method = Method.POST;
            request.AddUrlSegment("folder", folder);
            request.AddFile("file", content, safeName);

            _client.ExecuteAsync(request, x =>
            {
                var root = JsonConvert
                    .DeserializeXNode(x.Content, "root")
                    .Root;

                if (root == null)
                    return;

                var path = root.GetValue("id");

                if (safeName == name)
                {
                    path = GetSyncPath(path);
                    completed(path);
                }

                // Rename & overwrite
                Rename(path, name, completed);
            });
        }

        private string GetSyncPath(string id)
        {
            return string.Concat(id,
                Environment.NewLine, _token);
        }

        private void Rename(string path, string name,
            Action<string> completed)
        {
            var request = Request(path);
            request.Method = Method.PUT;

            request.JsonSerializer = new
                StupidAssemblyRedirectWorkAround(name);
            request.AddBody(new object());

            _client.ExecuteAsync(request, x =>
            {
                var root = JsonConvert
                    .DeserializeXNode(x.Content, "root")
                    .Root;

                if (root == null)
                    return;

                var id = root.GetValue("id");
                completed(GetSyncPath(id));
            });
        }

        private static RestRequest Request(string resource)
        {
            return new RestRequest(resource +
                "?access_token={token}")
            {
                RequestFormat = DataFormat.Json,
            };
        }

        private class StupidAssemblyRedirectWorkAround : RestSharp.Serializers.ISerializer
        {
            private readonly string _json;
            public string ContentType { get; set; }
            public string DateFormat { get; set; }
            public string Namespace { get; set; }
            public string RootElement { get; set; }

            public StupidAssemblyRedirectWorkAround(string name)
            {
                if (name == null)
                    throw new ArgumentNullException("name");

                _json = string.Format(
                    "{{name: \"{0}\"}}", name);
            }

            public string Serialize(object obj)
            {
                return _json;
            }
        }
    }
}