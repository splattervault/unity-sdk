using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SplatterVault.Players;

namespace SplatterVault.Core.Tests
{
    [TestClass]
    public class OpBuilderTests
    {
        [TestMethod]
        public void Body_IsWhatTheApiParses()
        {
            // PlayerDataController PATCH /me: { ops: [...] }, each op only the fields playerDataValidation.OP_FIELDS allows.
            var ops = new SvDataOps()
                .Set("settings", new { fov = 90, invertY = false }, ifVersion: 7)
                .Delete("tutorial")
                .Inc("coins", -50, min: 0)
                .Inc("stats", 1, field: "matches", max: 1000, ifVersion: 0);
            Assert.AreEqual(
                "{\"ops\":[" +
                "{\"op\":\"set\",\"store\":\"player\",\"key\":\"settings\",\"ifVersion\":7,\"value\":{\"fov\":90,\"invertY\":false}}," +
                "{\"op\":\"delete\",\"store\":\"player\",\"key\":\"tutorial\"}," +
                "{\"op\":\"inc\",\"store\":\"player\",\"key\":\"coins\",\"by\":-50,\"min\":0}," +
                "{\"op\":\"inc\",\"store\":\"player\",\"key\":\"stats\",\"ifVersion\":0,\"by\":1,\"field\":\"matches\",\"max\":1000}" +
                "]}",
                ops.ToJson());
        }

        [TestMethod]
        public void OnlyAllowedFields_NeverNulls()
        {
            var allowed = new Dictionary<string, string[]>
            {
                ["set"] = new[] { "op", "store", "key", "value", "ifVersion" },
                ["delete"] = new[] { "op", "store", "key", "ifVersion" },
                ["inc"] = new[] { "op", "store", "key", "by", "field", "min", "max", "ifVersion" }
            };
            var body = new SvDataOps().Set("a", "x").Delete("b").Inc("c", 2).ToBody();
            foreach (JObject op in (JArray)body["ops"])
            {
                var name = (string)op["op"];
                foreach (var p in op.Properties())
                {
                    CollectionAssert.Contains(allowed[name], p.Name);
                    Assert.AreNotEqual(JTokenType.Null, p.Value.Type, p.Name);
                }
            }
        }

        [TestMethod]
        public void Refusals_AreLocal()
        {
            Assert.ThrowsException<ArgumentException>(() => new SvDataOps().Set<string>("k", null));
            Assert.ThrowsException<ArgumentException>(() => new SvDataOps().Set("sv.reserved", 1));
            Assert.ThrowsException<ArgumentException>(() => new SvDataOps().Set(".dot", 1));
            Assert.ThrowsException<ArgumentException>(() => new SvDataOps().Set("__proto__", 1));
            Assert.ThrowsException<ArgumentException>(() => new SvDataOps().Inc("k", 1, min: 5, max: 1));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new SvDataOps().Delete("k", ifVersion: -1));
            var ops = new SvDataOps();
            for (var i = 0; i < SvDataOps.MaxOps; i++) ops.Delete("k" + i);
            Assert.ThrowsException<InvalidOperationException>(() => ops.Delete("one-too-many"));
            Assert.IsTrue(SvDataOps.IsValidKey("a.b:c-d_e"));
        }

        [TestMethod]
        public void AllHaveIfVersion()
        {
            Assert.IsFalse(new SvDataOps().AllHaveIfVersion);
            Assert.IsTrue(new SvDataOps().Set("a", 1, 0).Delete("b", 4).AllHaveIfVersion);
            Assert.IsFalse(new SvDataOps().Set("a", 1, 0).Delete("b").AllHaveIfVersion);
        }

        [TestMethod]
        public void NullMembers_InsideAValue_AreKept()
        {
            var json = new SvDataOps().Set("profile", new { name = (string)null, level = 3 }).ToJson();
            StringAssert.Contains(json, "\"value\":{\"name\":null,\"level\":3}");
        }
    }

    [TestClass]
    public class PlayerDataTests
    {
        private const string MeAnswer =
            "{\"data\":{\"player\":{\"settings\":{\"value\":{\"fov\":90,\"seen\":\"2026-11-13T19:00:00.000Z\"},\"version\":7,\"updatedAt\":\"2026-11-13T19:00:00.000Z\"}," +
            "\"matches\":{\"value\":41,\"version\":9,\"updatedAt\":\"2026-11-13T19:00:00.123Z\"}}," +
            "\"readonly\":{\"coins\":{\"value\":9007199254740991,\"version\":3,\"updatedAt\":null}}}}";

        [TestMethod]
        public async Task GetMine_QueryAndAnswer()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.Done(new SvHttpResponse(200, MeAnswer));
            var data = await kit.Sv.Data.GetMineAsync(new[] { "settings", "matches", "settings" }, keysOnly: true, stores: new[] { "player", "readonly" });
            Assert.AreEqual("/player-data/me?stores=player,readonly&keys=settings,matches&keysOnly=true", FakeTransport.PathOf(kit.Transport.Requests.Last()));
            Assert.AreEqual(7, data.Player.VersionOf("settings"));
            Assert.AreEqual(0, data.Player.VersionOf("missing"));
            Assert.AreEqual(41L, data.Player.Get<long>("matches"));
            Assert.AreEqual(9007199254740991L, data.Readonly.Get<long>("coins"));
            Assert.AreEqual("2026-11-13T19:00:00.123Z", data.Player["matches"].UpdatedAt, "dates are kept as sent");
            Assert.AreEqual(JTokenType.String, data.Player["settings"].Value["seen"].Type);
            Assert.AreEqual("2026-11-13T19:00:00.000Z", (string)data.Player["settings"].Value["seen"]);
            Assert.IsNull(data.Readonly["coins"].UpdatedAt);
        }

        [TestMethod]
        public async Task GetMine_RefusesStoresAPlayerCannotRead_AndTooManyKeys()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => kit.Sv.Data.GetMineAsync(stores: new[] { "internal" }));
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => kit.Sv.Data.GetMineAsync(Enumerable.Range(0, 101).Select(i => "k" + i)));
        }

        [TestMethod]
        public async Task Write_SendsPatch_AndReadsResults()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.Done(new SvHttpResponse(200,
                "{\"results\":[{\"store\":\"player\",\"key\":\"settings\",\"version\":8},{\"store\":\"player\",\"key\":\"old\",\"version\":null}," +
                "{\"store\":\"player\",\"key\":\"coins\",\"version\":12,\"value\":150}]}"));
            var result = await kit.Sv.Data.WriteAsync(new SvDataOps().Set("settings", 1).Delete("old").Inc("coins", 50));
            Assert.AreEqual("PATCH", kit.Transport.Requests.Last().Method);
            Assert.AreEqual(8L, result.Results[0].Version);
            Assert.IsNull(result.Results[1].Version);
            Assert.AreEqual(150L, (long)result.Results[2].Value);
        }

        [TestMethod]
        public async Task VersionConflict_DetailsAreReadable()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.Done(new SvHttpResponse(409,
                "{\"error\":\"version_conflict\",\"message\":\"x\",\"details\":[{\"store\":\"player\",\"key\":\"settings\",\"version\":9}]}"));
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() =>
                kit.Sv.Data.WriteAsync(new SvDataOps().Set("settings", 1, ifVersion: 7)));
            Assert.AreEqual(9L, e.DetailsAs<List<SvVersionInfo>>().Single().Version);
            Assert.AreEqual(1, kit.Transport.Requests.Count(r => r.Method == "PATCH"), "a conflict is an answer, not repeated");
        }

        [TestMethod]
        public async Task GameData_UsesTheETag_And304ReturnsTheKeptCopy()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            const string etag = "W/\"pd-abc\"";
            kit.Transport.Handler = r => Http.Done(r.Headers.TryGetValue("If-None-Match", out var inm) && inm == etag
                ? new SvHttpResponse(304, "", new Dictionary<string, string> { ["ETag"] = etag })
                : new SvHttpResponse(200, "{\"data\":{\"motd\":{\"value\":\"Season 2!\",\"version\":3,\"updatedAt\":\"2026-11-13T19:00:00.000Z\"}}}",
                    new Dictionary<string, string> { ["etag"] = etag }));
            var first = await kit.Sv.Data.GetGameDataAsync(new[] { "motd" });
            Assert.IsFalse(first.NotModified);
            Assert.AreEqual(etag, first.ETag);
            var second = await kit.Sv.Data.GetGameDataAsync(new[] { "motd" });
            Assert.IsTrue(second.NotModified);
            Assert.AreEqual("Season 2!", second.Data.Get<string>("motd"));
            var gets = kit.Transport.To("GET", "/player-data/game");
            Assert.IsFalse(gets[0].Headers.ContainsKey("If-None-Match"));
            Assert.AreEqual(etag, gets[1].Headers["If-None-Match"]);
            Assert.AreEqual("Bearer t1", gets[1].Headers["Authorization"]);
        }

        [TestMethod]
        public async Task GameData_NewETag_ReplacesTheKeptCopy()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            var version = 0;
            kit.Transport.Handler = r =>
            {
                version++;
                return Http.Done(new SvHttpResponse(200, "{\"data\":{\"motd\":{\"value\":\"v" + version + "\",\"version\":" + version + ",\"updatedAt\":null}}}",
                    new Dictionary<string, string> { ["ETag"] = "W/\"pd-" + version + "\"" }));
            };
            await kit.Sv.Data.GetGameDataAsync();
            var second = await kit.Sv.Data.GetGameDataAsync();
            Assert.AreEqual("v2", second.Data.Get<string>("motd"));
            Assert.AreEqual("W/\"pd-1\"", kit.Transport.Requests.Last().Headers["If-None-Match"]);
        }

        [TestMethod]
        public async Task PublicData_SendsNoCredential()
        {
            var kit = new Kit();
            await kit.SignInBySteamAsync();
            kit.Transport.Handler = r => Http.Done(new SvHttpResponse(200, "{\"data\":{}}", new Dictionary<string, string> { ["ETag"] = "W/\"pd-x\"" }));
            await kit.Sv.Data.GetPublicAsync();
            await kit.Sv.Data.GetPublicAsync("sys_123 key/x", new[] { "motd" });
            var requests = kit.Transport.To("GET", "/player-data/games/");
            Assert.AreEqual("/player-data/games/123/public", FakeTransport.PathOf(requests[0]));
            Assert.AreEqual("/player-data/games/sys_123%20key%2Fx/public?keys=motd", FakeTransport.PathOf(requests[1]));
            Assert.IsTrue(requests.All(r => !r.Headers.ContainsKey("Authorization")));
        }

        [TestMethod]
        public async Task PublicData_WorksSignedOut()
        {
            var kit = new Kit();
            kit.Transport.Handler = r => Http.Done(new SvHttpResponse(404, "{\"error\":\"game_not_found\",\"message\":\"No such game, or it has no public data.\"}"));
            var e = await Assert.ThrowsExceptionAsync<SvApiException>(() => kit.Sv.Data.GetPublicAsync());
            Assert.AreEqual("game_not_found", e.ErrorCode);
        }
    }

    [TestClass]
    public class JsonTests
    {
#pragma warning disable CS0649 // filled by the JSON reader
        private class Nested
        {
            public Inner a;
        }

        private class Inner
        {
            public List<JToken> b;
            public string when;
        }
#pragma warning restore CS0649

        [TestMethod]
        public void DatesStayStrings_Everywhere()
        {
            var token = SvJson.Parse("{\"when\":\"2026-11-13T19:00:00.000Z\",\"list\":[\"2026-01-01T00:00:00Z\"]}");
            Assert.AreEqual(JTokenType.String, token["when"].Type);
            Assert.AreEqual("2026-11-13T19:00:00.000Z", (string)token["when"]);
            Assert.AreEqual("{\"when\":\"2026-11-13T19:00:00.000Z\",\"list\":[\"2026-01-01T00:00:00Z\"]}", SvJson.Serialize(token));
            var txn = SvJson.Deserialize<SvStoreTxnResult>(Http.Txn(false).ToString(Formatting.None));
            Assert.AreEqual("2026-11-13T19:00:00.000Z", txn.Txn.CreatedAt);
            Assert.AreEqual("2026-11-13T19:00:00.000Z", txn.Items[0].AcquiredAt);
        }

        [TestMethod]
        public void BigNumbers_AndPlatformIds()
        {
            var t = SvJson.Parse("{\"max\":9007199254740991,\"steam\":\"76561198000000001\",\"digits\":76561198000000001,\"big\":123456789012345678901234567890}");
            Assert.AreEqual(9007199254740991L, (long)t["max"]);
            Assert.AreEqual(JTokenType.String, t["steam"].Type);
            Assert.AreEqual("76561198000000001", (string)t["steam"]);
            Assert.AreEqual(76561198000000001L, (long)t["digits"], "read exactly as Int64 (but send such ids as strings)");
            Assert.AreEqual("123456789012345678901234567890", t["big"].ToString(), "past Int64 stays exact (BigInteger)");
            var balance = SvJson.Deserialize<SvBalance>("{\"currency\":\"GOLD\",\"amount\":9007199254740991}");
            Assert.AreEqual(9007199254740991L, balance.Amount);
        }

        [TestMethod]
        public void NestedValues_ReadTyped()
        {
            var entry = SvJson.Deserialize<SvDataEntry>(
                "{\"value\":{\"a\":{\"b\":[1,2.5,{\"c\":true}],\"when\":\"2026-11-13T19:00:00.000Z\"}},\"version\":4,\"updatedAt\":\"2026-11-13T19:00:00.000Z\"}");
            var nested = entry.Get<Nested>();
            Assert.AreEqual(3, nested.a.b.Count);
            Assert.AreEqual(2.5, (double)nested.a.b[1]);
            Assert.IsTrue((bool)nested.a.b[2]["c"]);
            Assert.AreEqual("2026-11-13T19:00:00.000Z", nested.a.when);
            Assert.AreEqual(4L, entry.Version);
        }

        [TestMethod]
        public void Values_RoundTrip_ThroughTheBuilder()
        {
            var value = SvJson.Parse("{\"deep\":{\"x\":[1,{\"y\":\"2026-11-13T19:00:00.000Z\"}],\"n\":null,\"f\":0.1,\"u\":\"\\u00e9\"}}");
            var body = new SvDataOps().Set("k", value).ToBody();
            var sent = SvJson.Parse(SvJson.Serialize(body))["ops"][0]["value"];
            Assert.IsTrue(JToken.DeepEquals(value, sent));
        }

        [TestMethod]
        public void Parse_RefusesTrailingText()
        {
            Assert.ThrowsException<JsonReaderException>(() => SvJson.Parse("{\"a\":1} {\"b\":2}"));
        }

        [TestMethod]
        public void UnknownFields_AreIgnored_MissingOnesDefault()
        {
            var catalog = SvJson.Deserialize<SvStoreCatalog>(
                "{\"settings\":{\"playerPurchases\":true,\"playerConsume\":false,\"futureSwitch\":1},\"currencies\":[{\"code\":\"GOLD\",\"name\":\"Gold\",\"cap\":null}]," +
                "\"items\":[{\"sku\":\"sword_gold\",\"name\":\"Gold sword\",\"kind\":\"item\",\"stackable\":false,\"maxOwned\":1,\"prices\":[{\"currency\":\"GOLD\",\"amount\":500}]," +
                "\"contents\":[],\"tags\":[\"weapon\"],\"custom\":{\"tier\":3},\"purchasable\":true,\"consumable\":false,\"newField\":\"x\"}]}");
            Assert.IsTrue(catalog.Settings.PlayerPurchases);
            Assert.IsNull(catalog.Currencies[0].Cap);
            var sword = catalog.Find("sword_gold");
            Assert.AreEqual(500L, sword.PriceIn("GOLD"));
            Assert.IsNull(sword.PriceIn("GEMS"));
            Assert.AreEqual(1L, sword.MaxOwned);
            Assert.AreEqual(3, (int)sword.Custom["tier"]);
        }
    }
}
