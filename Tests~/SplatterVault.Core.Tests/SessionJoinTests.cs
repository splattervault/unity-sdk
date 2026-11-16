using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace SplatterVault.Core.Tests
{
    /// <summary>
    /// Where players join a session: GameSession.connection, GetJoinHost, GetJoinPort and the obsolete GetServerPort
    /// (Runtime/Models.cs, read with the same JSON settings as SplatterVaultClient).
    /// </summary>
    [TestClass]
    public class SessionJoinTests
    {
        // SplatterVaultClient.SerializerSettings
        private static readonly JsonSerializerSettings WireSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            FloatParseHandling = FloatParseHandling.Double
        };

        private static GameSession Read(string json)
        {
            var session = JsonConvert.DeserializeObject<GameSession>(json, WireSettings);
            Assert.IsNotNull(session);
            return session;
        }

        [TestMethod]
        public void FixedPortGame_JoinsOnTheConfiguredPort_NotTheRoomPort()
        {
            // Minecraft Java: server-port={{gamePort}} in server.properties. The server still leases a room port (8100).
            var session = Read(@"{
                ""id"": 501, ""status"": ""Active"", ""code"": ""ABCD"",
                ""slaveIp"": ""203.0.113.10"", ""slavePort"": 8100,
                ""connection"": {
                    ""host"": ""mc-501.example.net"", ""primaryPort"": 25565,
                    ""ports"": [ { ""id"": ""game"", ""label"": ""Game Port"", ""port"": 25565, ""protocol"": ""tcp"" },
                                 { ""id"": ""query"", ""label"": ""Query"", ""port"": 25565, ""protocol"": ""udp"" } ],
                    ""password"": ""ABCD"", ""code"": ""ABCD"", ""serverName"": ""Friday build"",
                    ""connectionMode"": ""direct_ip"", ""launchUrl"": null, ""showHost"": true, ""showPassword"": false,
                    ""version"": ""1.21.4"", ""platform"": ""PC"",
                    ""instructions"": { ""summary"": ""Join mc-501.example.net:25565"", ""steps"": [ ""Multiplayer > Add Server"", ""mc-501.example.net:25565"" ] }
                }
            }");

            Assert.AreEqual(25565, session.GetJoinPort());
            Assert.AreEqual("mc-501.example.net", session.GetJoinHost());
            Assert.AreEqual(8100, session.slavePort, "slavePort is read unchanged");
            Assert.AreEqual("203.0.113.10", session.slaveIp, "slaveIp is read unchanged");

            var c = session.connection;
            Assert.IsNotNull(c);
            Assert.AreEqual(2, c.ports.Count);
            Assert.AreEqual("game", c.ports[0].id);
            Assert.AreEqual(25565, c.ports[0].port);
            Assert.AreEqual("udp", c.ports[1].protocol);
            Assert.AreEqual("ABCD", c.code);
            Assert.AreEqual("Friday build", c.serverName);
            Assert.AreEqual("direct_ip", c.connectionMode);
            Assert.IsNull(c.launchUrl);
            Assert.IsTrue(c.showHost);
            Assert.IsFalse(c.showPassword);
            Assert.AreEqual("1.21.4", c.version);
            Assert.AreEqual("PC", c.platform);
            Assert.AreEqual("Join mc-501.example.net:25565", c.instructions.summary);
            Assert.AreEqual(2, c.instructions.steps.Count);
        }

        [TestMethod]
        public void RoomPortGame_JoinsOnTheRoomPort()
        {
            // A game launched with {{slavePort}}: the API's primaryPort is the session's room port (here 8101).
            var session = Read(@"{
                ""id"": 502, ""status"": ""Active"", ""slaveIp"": ""203.0.113.11"", ""slavePort"": 8101,
                ""connection"": { ""host"": ""203.0.113.11"", ""primaryPort"": 8101,
                    ""ports"": [ { ""id"": ""game"", ""label"": ""Game Port"", ""port"": 8101 } ],
                    ""showHost"": true, ""showPassword"": true, ""instructions"": null }
            }");

            Assert.AreEqual(8101, session.GetJoinPort());
            Assert.AreEqual("203.0.113.11", session.GetJoinHost());
            Assert.IsNull(session.connection.instructions);
            Assert.IsNull(session.connection.ports[0].protocol);
        }

        [TestMethod]
        public void ConnectionWithNullPort_GivesNull_NotTheRoomPortOrAGuess()
        {
            // A room-port game right after a pool claim (the room has not registered yet), or a game with no port.
            // slavePort is set here only to show it is not used once the API sends connection details.
            var session = Read(@"{
                ""id"": 503, ""status"": ""Active"", ""slaveIp"": ""203.0.113.12"", ""slavePort"": 8100,
                ""connection"": { ""host"": ""203.0.113.12"", ""primaryPort"": null, ""ports"": [],
                    ""password"": null, ""code"": null, ""serverName"": null, ""connectionMode"": ""direct_ip"",
                    ""launchUrl"": null, ""showHost"": true, ""showPassword"": true, ""version"": null, ""platform"": null,
                    ""instructions"": null }
            }");

            Assert.IsNotNull(session.connection);
            Assert.IsNull(session.connection.primaryPort);
            Assert.IsNull(session.GetJoinPort());
            Assert.AreEqual(0, session.connection.ports.Count);
        }

        [TestMethod]
        public void NoConnection_FallsBackToSlaveIpAndSlavePort()
        {
            // An API that sends no connection details on this route.
            var absent = Read(@"{ ""id"": 504, ""status"": ""Active"", ""slaveIp"": ""203.0.113.13"", ""slavePort"": 8102 }");
            Assert.IsNull(absent.connection);
            Assert.AreEqual(8102, absent.GetJoinPort());
            Assert.AreEqual("203.0.113.13", absent.GetJoinHost());

            // connection: null (the server has no address yet), nothing known.
            var pending = Read(@"{ ""id"": 505, ""status"": ""Pending"", ""slaveIp"": null, ""slavePort"": null, ""connection"": null }");
            Assert.IsNull(pending.connection);
            Assert.IsNull(pending.GetJoinPort());
            Assert.IsNull(pending.GetJoinHost());
        }

        [TestMethod]
        public void ConnectionWithoutHost_JoinHostFallsBackToSlaveIp()
        {
            var session = new GameSession
            {
                slaveIp = "203.0.113.14",
                connection = new SessionConnectionInfo { host = "", primaryPort = 2456 }
            };
            Assert.AreEqual("203.0.113.14", session.GetJoinHost());
            Assert.AreEqual(2456, session.GetJoinPort());
        }

        [TestMethod]
        public void GetServerPort_IsObsolete_AndStillWorks()
        {
            var method = typeof(GameSession).GetMethod(nameof(GameSession.GetJoinPort));
            Assert.IsNotNull(method);
#pragma warning disable CS0618 // the obsolete method is what is checked here
            var obsolete = typeof(GameSession).GetMethod(nameof(GameSession.GetServerPort)).GetCustomAttribute<ObsoleteAttribute>();
            Assert.IsNotNull(obsolete, "GetServerPort should be marked [Obsolete]");
            Assert.IsFalse(obsolete.IsError, "GetServerPort must still compile (a warning, not an error)");
            StringAssert.Contains(obsolete.Message, "GetJoinPort");

            // Known join port: the same answer as GetJoinPort.
            var fixedPort = new GameSession { slavePort = 8100, connection = new SessionConnectionInfo { host = "h", primaryPort = 25565 } };
            Assert.AreEqual(25565, fixedPort.GetServerPort());

            // No connection details: slavePort, as before.
            Assert.AreEqual(9000, new GameSession { slavePort = 9000 }.GetServerPort());

            // Not known: 8100, as before.
            Assert.AreEqual(8100, new GameSession().GetServerPort());
            Assert.AreEqual(8100, new GameSession { slavePort = 8101, connection = new SessionConnectionInfo { host = "h" } }.GetServerPort());
#pragma warning restore CS0618
        }

        [TestMethod]
        public void PoolClaimAnswer_ReadsConnectionWithNullPort()
        {
            var claim = JsonConvert.DeserializeObject<PoolClaimResponse>(@"{
                ""sessionId"": 506, ""serverInstanceId"": 77,
                ""connection"": { ""host"": ""203.0.113.15"", ""primaryPort"": null, ""ports"": [], ""showHost"": true, ""showPassword"": true }
            }", WireSettings);
            Assert.IsNotNull(claim);
            Assert.AreEqual(506, claim.sessionId);
            Assert.IsNull(claim.connection.primaryPort);
        }
    }
}
