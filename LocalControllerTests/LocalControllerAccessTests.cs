/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of LocalController <https://github.com/OpenChargingCloud/LocalController>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using System.Net;
using System.Text;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.Mail;

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// Who may do what on a local controller: its two resources beside the
    /// node's, its CPO beside the node's viewer and administrators - and a
    /// role from the configuration file, heard by the API like every other.
    /// </summary>
    public class LocalControllerAccessTests
    {

        #region Data

        private String            directory   = "";
        private LocalController?  controller;
        private Uri?              address;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {
            directory = TestControllers.TemporaryDirectory("access");
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public async Task TakeItAwayAgain()
        {

            if (controller is not null)
                await controller.DisposeAsync();

            controller = null;

            TestControllers.Remove(directory);

        }

        #endregion


        #region (private) AController(Configuration = null)

        /// <summary>
        /// A local controller with the given configuration file, off the
        /// network: made, and not yet started.
        /// </summary>
        private LocalController AController(JObject? Configuration = null)
        {

            controller  = TestControllers.New(directory, Configuration ?? TestControllers.Offline);
            address     = new Uri(controller.WebInterfaceURL.ToString());

            return controller;

        }

        #endregion

        #region (private) SignedInAs(Name, Role)

        /// <summary>
        /// A browser signed in with a password as an account of the given name,
        /// made for the purpose and put in the group of the given role, or in
        /// none - made the way the controller makes its first one, so that it
        /// may sign in.
        /// </summary>
        private async Task<HttpClient> SignedInAs(String   Name,
                                                  String?  Role)
        {

            var password = "correct-horse-battery-" + Guid.NewGuid().ToString("N")[..8];

            Assert.That(controller!.ExtAPI.TryGetOrganization(Organization_Id.Parse("LocalController"), out var organization) &&
                        organization is Organization, Is.True, "the controller's organization is not there");

            var account = await controller.ExtAPI.CreateUser(
                                    User_Id.Parse(Name),
                                    I18NString.Create(Languages.en, Name),
                                    SimpleEMailAddress.Parse($"{Name}@localhost"),
                                    User2OrganizationEdgeLabel.IsMember,
                                    (Organization) organization!,
                                    Password:                  password,
                                    SkipDefaultNotifications:  true,
                                    SkipNewUserEMail:          true,
                                    SkipNewUserNotifications:  true,
                                    AcceptedEULA:              DateTimeOffset.UtcNow.AddSeconds(-1),
                                    IsAuthenticated:           true
                                );

            Assert.That(account,                                                          Is.Not.Null, $"the account '{Name}' was not made");
            Assert.That(controller.ExtAPI.TryGetUser(User_Id.Parse(Name), out var stored),  Is.True);

            if (Role is not null)
            {

                Assert.That(controller.ExtAPI.TryGetUserGroup(UserGroup_Id.Parse(Role), out var group), Is.True,
                            $"the controller has no group '{Role}'");

                var joined = await controller.ExtAPI.AddUserToUserGroup((User) stored!, User2UserGroupEdgeLabel.IsMember, (UserGroup) group!);

                Assert.That(joined.IsSuccess, Is.True, $"'{Name}' could not be put in '{Role}'");

            }

            // Signed in the way a browser is, at the HTTPExt API, and carrying
            // the session cookie from there on: one password check rather than
            // one per request.
            var client   = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true }) {
                               BaseAddress  = address,
                               Timeout      = TimeSpan.FromSeconds(30)
                           };

            var signedIn = await client.PostAsync($"{LocalController.ExtAPIPath.ToString().TrimEnd('/')}/login",
                                                  new FormUrlEncodedContent([
                                                      new KeyValuePair<String, String>("login",     Name),
                                                      new KeyValuePair<String, String>("password",  password)
                                                  ]));

            Assert.That(signedIn.IsSuccessStatusCode, Is.True, $"'{Name}' could not sign in: {(Int32) signedIn.StatusCode}");

            return client;

        }

        #endregion

        #region (private static) Post(Client, Path, JSON)

        private static Task<HttpResponseMessage> Post(HttpClient  Client,
                                                      String      Path,
                                                      String      JSON)

            => Client.PostAsync(Path, new StringContent(JSON, Encoding.UTF8, "application/json"));

        #endregion

        #region (private static) LocalControllerAccessControl()

        /// <summary>
        /// The controller's resources and roles, as a node told nothing else
        /// puts them together.
        /// </summary>
        private static AccessControl LocalControllerAccessControl()
        {

            Assert.That(AccessControl.TryCombine(LocalControllerAccess.Resources, LocalControllerAccess.Roles, null, null,
                                                 "local controller", out var access, out _, out var error),
                        Is.True, error);

            return access!;

        }

        #endregion


        #region ALocalControllerKnowsItsResourcesAndItsThreeRoles()

        /// <summary>
        /// The node brings the viewer and the administrators, and the local
        /// controller its CPO - and the line up to the CSMS and the charging
        /// stations as resources beside the node's.
        /// </summary>
        [Test]
        public void ALocalControllerKnowsItsResourcesAndItsThreeRoles()
        {

            var lc = AController();

            Assert.Multiple(() => {
                Assert.That(lc.Roles,             Is.EqualTo(new[] { "viewer", "cpo", WWCPNode.AdminRole }));
                Assert.That(lc.Access.Resources,  Is.EqualTo(new[] { "configuration", "dns", "nts", "certificates", "csms", "stations" }));
            });

        }

        #endregion

        #region EachRoleMayDoWhatItAlwaysMayDo(Role, Permission, Allowed)

        /// <summary>
        /// What each role could do before roles were data, permission by
        /// permission: the viewer looks, the CPO runs the site - its name and
        /// time servers, the line up to the CSMS and the charging stations -
        /// and only the administrators touch the certificates.
        /// </summary>
        [TestCase("viewer",       "configuration:read",  true)]
        [TestCase("viewer",       "dns:read",            true)]
        [TestCase("viewer",       "csms:read",           true)]
        [TestCase("viewer",       "stations:read",       true)]
        [TestCase("viewer",       "certificates:read",   true)]
        [TestCase("viewer",       "dns:edit",            false)]
        [TestCase("viewer",       "dns:run",             false)]
        [TestCase("viewer",       "csms:edit",           false)]
        [TestCase("viewer",       "stations:edit",       false)]
        [TestCase("viewer",       "certificates:edit",   false)]

        [TestCase("cpo",          "certificates:read",   true)]
        [TestCase("cpo",          "dns:edit",            true)]
        [TestCase("cpo",          "dns:run",             true)]
        [TestCase("cpo",          "nts:edit",            true)]
        [TestCase("cpo",          "nts:run",             true)]
        [TestCase("cpo",          "csms:edit",           true)]
        [TestCase("cpo",          "stations:edit",       true)]
        [TestCase("cpo",          "certificates:edit",   false)]
        [TestCase("cpo",          "configuration:edit",  false)]

        [TestCase("systemadmin",  "certificates:edit",   true)]
        [TestCase("systemadmin",  "stations:edit",       true)]
        [TestCase("systemadmin",  "csms:edit",           true)]
        public void EachRoleMayDoWhatItAlwaysMayDo(String Role, String Permission, Boolean Allowed)
        {

            Assert.That(protocols.WWCP.Node.Web.Permission.TryParse(Permission, out var permission, out var error), Is.True, error);

            Assert.That(LocalControllerAccessControl().RoleNamed(Role)!.Allows(permission.Resource, permission.Operation), Is.EqualTo(Allowed));

        }

        #endregion


        #region ACPOMayLookAtTheCertificatesAndIsToldWhoMayChangeThem()

        /// <summary>
        /// Over the wire, as a browser signed in as a CPO sees it: the page
        /// opens, a signing request is refused with the role to ask for, and
        /// what the browser is told it may do says the same beforehand.
        /// </summary>
        [Test]
        public async Task ACPOMayLookAtTheCertificatesAndIsToldWhoMayChangeThem()
        {

            await AController().Start();

            using var cpo    = await SignedInAs("cpo1", "cpo");

            var looked       = await cpo.GetAsync("api/v1/configuration/ocpp-server/certificates");
            var requested    = await Post(cpo, "api/v1/configuration/ocpp-server/certificates", "{}");
            var refusal      = await requested.Content.ReadAsStringAsync();
            var me           = JObject.Parse(await (await cpo.GetAsync("api/v1/auth/me")).Content.ReadAsStringAsync());
            var permissions  = me["permissions"]!.Values<String>().OfType<String>().ToArray();

            Assert.Multiple(() => {
                Assert.That(looked.StatusCode,               Is.EqualTo(HttpStatusCode.OK));
                Assert.That(requested.StatusCode,            Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(refusal,                         Does.Contain("This needs the systemadmin role."));
                Assert.That(me["roles"]!.Values<String>(),   Is.EqualTo(new[] { "cpo" }));
                Assert.That(permissions,                     Does.Contain("stations:edit").And.Contain("csms:edit").And.Contain("dns:run").And.Contain("certificates:read"));
                Assert.That(permissions,                     Does.Not.Contain("certificates:edit").And.Not.Contain("configuration:edit"));
                Assert.That(permissions.Any(permission => permission.StartsWith('*')),
                            Is.False,
                            "spelt out resource by resource, so that a page asking \"dns:read\" need not know what \"*\" is");
            });

        }

        #endregion

        #region ARoleFromTheConfigurationFileIsHeardByTheAPI()

        /// <summary>
        /// A role nobody compiled in: the file names it - with a resource of
        /// the local controller's own beside one of the node's - the start
        /// makes its group, and a route asking for a permission lets it in or
        /// not by what the file says it carries.
        /// </summary>
        [Test]
        public async Task ARoleFromTheConfigurationFileIsHeardByTheAPI()
        {

            await AController(new JObject(
                                  new JProperty("nts",    new JObject(new JProperty("enabled", false))),
                                  new JProperty("roles",  new JObject(
                                      new JProperty("support", new JArray("dns:read", "stations:read"))
                                  ))
                              )).Start();

            using var support  = await SignedInAs("supporter", "support");

            var dns            = await support.GetAsync("api/v1/configuration/dns");
            var stations       = await support.GetAsync("api/v1/configuration/ocpp-server/stations");
            var csms           = await support.GetAsync("api/v1/configuration/csms");
            var refusal        = await csms.Content.ReadAsStringAsync();

            Assert.Multiple(() => {
                Assert.That(controller!.Roles,   Is.EqualTo(new[] { "viewer", "cpo", "support", WWCPNode.AdminRole }));
                Assert.That(dns.StatusCode,      Is.EqualTo(HttpStatusCode.OK));
                Assert.That(stations.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(csms.StatusCode,     Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(refusal,             Does.Contain("This needs the viewer or cpo or systemadmin role."),
                            "the file's role carries dns:read and stations:read and nothing else, so it is not among the ones to ask for");
            });

        }

        #endregion

        #region ARoleNamingAResourceThisControllerDoesNotHaveStopsTheStart()

        /// <summary>
        /// A role in the file that names a resource the local controller does
        /// not have - a vehicle's, say - stops it being built, and says which
        /// resources it does have: the node's, and the two it hands in.
        /// </summary>
        [Test]
        public void ARoleNamingAResourceThisControllerDoesNotHaveStopsTheStart()
        {

            var refused = Assert.Throws<InvalidOperationException>(() => AController(new JObject(
                              new JProperty("nts",    new JObject(new JProperty("enabled", false))),
                              new JProperty("roles",  new JObject(
                                  new JProperty("support", new JArray("vehicle:read"))
                              ))
                          )));

            Assert.That(refused!.Message, Does.Contain("'vehicle'").And.Contain("csms").And.Contain("stations"));

        }

        #endregion

        #region EveryRouteAsksForItsOwnPermission(Method, Path, Permission)

        /// <summary>
        /// Every route of the API, and the one permission it asks for - read
        /// off the refusal an account in no role at all is given, which names
        /// every role that may. The configuration file adds a role for each
        /// operation on each resource, "r-dns-edit" and so on, so that the
        /// refusal names exactly one of them: the permission the route asks.
        /// </summary>
        /// <remarks>
        /// Refusals only: an account in no role is turned away before anything
        /// is read, so a route that would synchronise the clock or remove a
        /// certificate does neither here.
        /// </remarks>
        [TestCase("GET",     "api/v1/configuration",                                     "configuration:read")]
        [TestCase("GET",     "api/v1/configuration/dns",                                 "dns:read")]
        [TestCase("PUT",     "api/v1/configuration/dns",                                 "dns:edit")]
        [TestCase("POST",    "api/v1/configuration/dns/query",                           "dns:run")]
        [TestCase("GET",     "api/v1/configuration/nts",                                 "nts:read")]
        [TestCase("PUT",     "api/v1/configuration/nts",                                 "nts:edit")]
        [TestCase("POST",    "api/v1/configuration/nts/sync",                            "nts:run")]
        [TestCase("POST",    "api/v1/configuration/nts/test",                            "nts:run")]
        [TestCase("GET",     "api/v1/configuration/csms",                                "csms:read")]
        [TestCase("PUT",     "api/v1/configuration/csms",                                "csms:edit")]
        [TestCase("PUT",     "api/v1/configuration/csms/credentials",                    "csms:edit")]
        [TestCase("DELETE",  "api/v1/configuration/csms/credentials",                    "csms:edit")]
        [TestCase("GET",     "api/v1/configuration/ocpp-server",                         "stations:read")]
        [TestCase("PUT",     "api/v1/configuration/ocpp-server",                         "stations:edit")]
        [TestCase("GET",     "api/v1/configuration/ocpp-server/stations",                "stations:read")]
        [TestCase("POST",    "api/v1/configuration/ocpp-server/stations",                "stations:edit")]
        [TestCase("PUT",     "api/v1/configuration/ocpp-server/stations/cs001",          "stations:edit")]
        [TestCase("DELETE",  "api/v1/configuration/ocpp-server/stations/cs001",          "stations:edit")]
        [TestCase("PUT",     "api/v1/configuration/ocpp-server/stations/cs001/totp",     "stations:edit")]
        [TestCase("DELETE",  "api/v1/configuration/ocpp-server/stations/cs001/totp",     "stations:edit")]
        [TestCase("DELETE",  "api/v1/configuration/ocpp-server/stations/cs001/password", "stations:edit")]
        [TestCase("POST",    "api/v1/configuration/ocpp-server/groups",                  "stations:edit")]
        [TestCase("PUT",     "api/v1/configuration/ocpp-server/groups/site",             "stations:edit")]
        [TestCase("DELETE",  "api/v1/configuration/ocpp-server/groups/site",             "stations:edit")]
        [TestCase("GET",     "api/v1/configuration/ocpp-server/certificates",            "certificates:read")]
        [TestCase("POST",    "api/v1/configuration/ocpp-server/certificates",            "certificates:edit")]
        [TestCase("GET",     "api/v1/configuration/ocpp-server/certificates/k1/csr",     "certificates:read")]
        [TestCase("PUT",     "api/v1/configuration/ocpp-server/certificates/k1",         "certificates:edit")]
        [TestCase("DELETE",  "api/v1/configuration/ocpp-server/certificates/k1",         "certificates:edit")]
        [TestCase("GET",     "api/v1/configuration/ocpp-server/trust",                   "certificates:read")]
        [TestCase("POST",    "api/v1/configuration/ocpp-server/trust",                   "certificates:edit")]
        [TestCase("PUT",     "api/v1/configuration/ocpp-server/trust/t1",                "certificates:edit")]
        [TestCase("DELETE",  "api/v1/configuration/ocpp-server/trust/t1",                "certificates:edit")]
        [TestCase("GET",     "api/v1/certificates",                                      "certificates:read")]
        [TestCase("POST",    "api/v1/certificates",                                      "certificates:edit")]
        [TestCase("POST",    "api/v1/certificates/reload",                               "certificates:edit")]
        [TestCase("GET",     "api/v1/certificates/c1",                                   "certificates:read")]
        [TestCase("PATCH",   "api/v1/certificates/c1",                                   "certificates:edit")]
        [TestCase("DELETE",  "api/v1/certificates/c1",                                   "certificates:edit")]
        public async Task EveryRouteAsksForItsOwnPermission(String Method, String Path, String Permission)
        {

            var roles = new JObject();

            foreach (var resource in new[] { "configuration", "dns", "nts", "certificates", "csms", "stations" })
                foreach (var operation in new[] { "read", "edit", "run" })
                    roles.Add($"r-{resource}-{operation}", new JArray($"{resource}:{operation}"));

            await AController(new JObject(
                                  new JProperty("nts",    new JObject(new JProperty("enabled", false))),
                                  new JProperty("roles",  roles)
                              )).Start();

            using var nobody  = await SignedInAs("nobody1", null);

            var response      = await nobody.SendAsync(new HttpRequestMessage(new HttpMethod(Method), Path) {
                                                           Content = Method == "GET"
                                                                         ? null
                                                                         : new StringContent("{}", Encoding.UTF8, "application/json")
                                                       });

            var refusal       = await response.Content.ReadAsStringAsync();

            // "This needs the r-dns-edit or cpo or systemadmin role." - the roles
            // of the file's that may, of which there has to be exactly one.
            var named         = System.Text.RegularExpressions.Regex.Matches(refusal, @"\br-[a-z]+-[a-z]+\b").
                                                                 Select(match => match.Value).
                                                                 Distinct().
                                                                 ToArray();

            Assert.Multiple(() => {
                Assert.That(response.StatusCode,  Is.EqualTo(HttpStatusCode.Forbidden), refusal);
                Assert.That(named,                Is.EqualTo(new[] { "r-" + Permission.Replace(':', '-') }),
                            $"{Method} {Path} asks for something else than {Permission}: {refusal}");
            });

        }

        #endregion

        #region TheClockIsForAnybodySignedIn()

        /// <summary>
        /// Whether the time here is worth anything is for anybody signed in, as
        /// the log and the event stream are - an account in no role at all
        /// included. Everything that is a resource is not.
        /// </summary>
        [Test]
        public async Task TheClockIsForAnybodySignedIn()
        {

            await AController().Start();

            using var nobody   = await SignedInAs("nobody1", null);

            var clock          = await nobody.GetAsync("api/v1/configuration/time");
            var configuration  = await nobody.GetAsync("api/v1/configuration");
            var refusal        = await configuration.Content.ReadAsStringAsync();

            Assert.Multiple(() => {
                Assert.That(clock.StatusCode,          Is.EqualTo(HttpStatusCode.OK));
                Assert.That(configuration.StatusCode,  Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(refusal,                   Does.Contain("This needs the viewer or cpo or systemadmin role."));
            });

        }

        #endregion

    }

}
