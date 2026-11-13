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

using Newtonsoft.Json.Linq;
using NUnit.Framework;

using cloud.charging.open.protocols.WWCP;
using cloud.charging.open.protocols.WWCP.NetworkingNode;
using cloud.charging.open.protocols.WWCP.Node.Web;

using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// Who may sign what is sent to the local controller itself: its own roles,
    /// with what its configuration says they may do, held against its own table
    /// (white paper "Signed OCPP Messages and Signer Roles", section 5.3).
    /// </summary>
    /// <remarks>
    /// Each test signs a request as a CSMS's adapter does, takes what goes on the
    /// wire, parses it as the controller does and verifies it with the
    /// controller's own signature policy, as its adapter does. Nothing is sent
    /// over a network; the controller is made and not started.
    /// </remarks>
    public class SignaturePolicyTests
    {

        #region Data

        private String            directory   = "";
        private LocalController?  controller;

        private static Int32      requestIds  = 200000;

        private static readonly KeyPair support     = ECCKeyPair.GenerateKeys()!;
        private static readonly KeyPair cpo         = ECCKeyPair.GenerateKeys()!;
        private static readonly KeyPair admin       = ECCKeyPair.GenerateKeys()!;
        private static readonly KeyPair resetter    = ECCKeyPair.GenerateKeys()!;
        private static readonly KeyPair dns         = ECCKeyPair.GenerateKeys()!;
        private static readonly KeyPair stranger    = ECCKeyPair.GenerateKeys()!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {
            directory = TestControllers.TemporaryDirectory("signatures");
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


        #region (private) AController(Mode, Roles = null)

        /// <summary>
        /// A local controller off the network whose signature policy is in the
        /// given mode, and that knows the test keys by its roles - and, from its
        /// configuration file, a role "resetter" that may restart it once idle.
        /// </summary>
        private SignaturePolicy AController(MessagePolicyModes Mode)
        {

            var configuration = (JObject) TestControllers.Offline.DeepClone();

            configuration["roles"] = new JObject(
                                         new JProperty("resetter",     new JArray("reset:run")),
                                         new JProperty("nameservers",  new JArray("dns:edit", "dns:read"))
                                     );

            controller = TestControllers.New(directory, configuration);

            var signers = new (KeyPair Key, String Role)[] {
                              (support,   "support"),
                              (cpo,       "cpo"),
                              (admin,     "systemadmin"),
                              (resetter,  "resetter"),
                              (dns,       "nameservers"),
                              (stranger,  "nobody-here")
                          };

            var policy = controller.Node.OCPP.SignaturePolicy;

            policy.Mode           = Mode;

            // Who a key belongs to is the node's user roles, in the library; what
            // is tested here is what the controller does with the role's name.
            policy.SignerRolesOf  = signature => signers.Where (signer => signature.KeyId.SequenceEqual(signer.Key.PublicKeyBytes)).
                                                         Select(signer => signer.Role);

            return policy;

        }

        #endregion

        #region (private) Sent(...) / Accepts(...)

        private SourceRouting ToTheController
            => SourceRouting.To(controller!.Node.Id);

        private static Request_Id NextRequestId
            => Request_Id.Parse(Interlocked.Increment(ref requestIds).ToString());

        /// <summary>
        /// Sign the request as a CSMS does, parse what was on the wire as the
        /// controller does, and say whether the controller's policy lets it on
        /// to its handler.
        /// </summary>
        private static Boolean Accepts<TRequest>(SignaturePolicy                    Policy,
                                                 TRequest                           Request,
                                                 Func<TRequest, Boolean, JObject>   ToJSON,
                                                 Func<JObject, TRequest>            Parse,
                                                 out String?                        ErrorResponse,
                                                 params KeyPair[]                   SignedBy)

            where TRequest : IRequest

            => Accepts(Policy, Request, ToJSON, Parse, out ErrorResponse, out _, SignedBy);

        /// <summary>
        /// The same, handing out the request as the controller received it, with
        /// its signatures verified: what its device model decides entry by entry.
        /// </summary>
        private static Boolean Accepts<TRequest>(SignaturePolicy                    Policy,
                                                 TRequest                           Request,
                                                 Func<TRequest, Boolean, JObject>   ToJSON,
                                                 Func<JObject, TRequest>            Parse,
                                                 out String?                        ErrorResponse,
                                                 out TRequest                       Received,
                                                 params KeyPair[]                   SignedBy)

            where TRequest : IRequest

        {

            if (SignedBy.Length > 0)
                Assert.That(new SignaturePolicy().
                                BindTo(NetworkingNode_Id.CSMS, OCPPv2_1.Version.WebSocketSubProtocolId).
                                SignRequestMessage(Request, ToJSON(Request, true), out var signingError, [.. SignedBy.Select(keyPair => keyPair.ToSignInfo1())]),
                            Is.True,
                            signingError);

            var onTheWire  = ToJSON(Request, false);
            Received       = Parse(onTheWire);

            return Policy.VerifyRequestMessage(Received, ToJSON(Received, true), out ErrorResponse, onTheWire);

        }

        /// <summary>
        /// A SetVariables to the controller, signed by the given keys, as the
        /// controller received it - and whether its policy let it on to the
        /// device model.
        /// </summary>
        private Boolean AcceptsASetVariables(SignaturePolicy                             Policy,
                                             out String?                                 ErrorResponse,
                                             out OCPPv2_1.CSMS.SetVariablesRequest       Received,
                                             params KeyPair[]                            SignedBy)
        {

            var requestId    = NextRequestId;
            var destination  = ToTheController;

            return Accepts(Policy,
                           new OCPPv2_1.CSMS.SetVariablesRequest(
                               destination,
                               [
                                   new OCPPv2_1.SetVariableData(
                                       Component:       new OCPPv2_1.Component(Name: "DNSClientController"),
                                       Variable:        new OCPPv2_1.Variable (Name: "Servers"),
                                       AttributeValue:  "192.0.2.53"
                                   )
                               ],
                               RequestId: requestId
                           ),
                           (request, withContext) => request.ToJSON(withContext),
                           json => OCPPv2_1.CSMS.SetVariablesRequest.Parse(json, requestId, destination, NetworkPath.Empty),
                           out ErrorResponse,
                           out Received,
                           SignedBy);

        }

        /// <summary>
        /// Whether the controller's policy lets the signers of the given request
        /// do the given operation on the given component, and why not.
        /// </summary>
        private static (Boolean Applied, String? Reason) Entry(SignaturePolicy  Policy,
                                                               IRequest         Request,
                                                               String           Component,
                                                               String           Variable,
                                                               String           Operation  = "edit",
                                                               String?          Instance   = null)

            => (Policy.DecideVariable(Request, Component, Variable, Instance, null, Operation, out var reason), reason);

        private Boolean AcceptsAReset(SignaturePolicy Policy, OCPPv2_1.ResetType Type, out String? ErrorResponse, params KeyPair[] SignedBy)
        {

            var requestId    = NextRequestId;
            var destination  = ToTheController;

            return Accepts(Policy,
                           new OCPPv2_1.CSMS.ResetRequest(destination, Type, RequestId: requestId),
                           (request, withContext) => request.ToJSON(withContext),
                           json => OCPPv2_1.CSMS.ResetRequest.Parse(json, requestId, destination, NetworkPath.Empty),
                           out ErrorResponse,
                           SignedBy);

        }

        private Boolean AcceptsATrigger(SignaturePolicy Policy, OCPPv2_1.MessageTrigger Message, out String? ErrorResponse, params KeyPair[] SignedBy)
        {

            var requestId    = NextRequestId;
            var destination  = ToTheController;

            return Accepts(Policy,
                           new OCPPv2_1.CSMS.TriggerMessageRequest(destination, Message, RequestId: requestId),
                           (request, withContext) => request.ToJSON(withContext),
                           json => OCPPv2_1.CSMS.TriggerMessageRequest.Parse(json, requestId, destination, NetworkPath.Empty),
                           out ErrorResponse,
                           SignedBy);

        }

        private Boolean AcceptsACertificateDeletion(SignaturePolicy Policy, out String? ErrorResponse, params KeyPair[] SignedBy)
        {

            var requestId    = NextRequestId;
            var destination  = ToTheController;

            return Accepts(Policy,
                           new OCPPv2_1.CSMS.DeleteCertificateRequest(
                               destination,
                               new OCPPv2_1.CertificateHashData(
                                   OCPPv2_1.HashAlgorithm.SHA256,
                                   "f2311e9a995dfbd006bfc909e480987dc2d440ae6eaf1746efdadc638a295f65",
                                   "99084534bbe5f6ceaffa2e65ff1ad5301c4c359b599d6edd486a475071f715fb",
                                   "23"
                               ),
                               RequestId: requestId
                           ),
                           (request, withContext) => request.ToJSON(withContext),
                           json => OCPPv2_1.CSMS.DeleteCertificateRequest.Parse(json, requestId, destination, NetworkPath.Empty),
                           out ErrorResponse,
                           SignedBy);

        }

        #endregion


        #region WhileThePolicyIsOffNothingAsksWhoSigned()

        /// <summary>
        /// "Off" is the default: a controller behaves as before it had a table,
        /// and an unsigned restart at once of a standard CSMS passes.
        /// </summary>
        [Test]
        public void WhileThePolicyIsOffNothingAsksWhoSigned()
        {

            controller = TestControllers.New(directory, TestControllers.Offline);

            var policy = controller.Node.OCPP.SignaturePolicy;

            Assert.Multiple(() => {
                Assert.That(policy.Mode,                                                                      Is.EqualTo(MessagePolicyModes.Off));
                Assert.That(AcceptsAReset(policy, OCPPv2_1.ResetType.Immediate, out var errorResponse),     Is.True, errorResponse);
            });

        }

        #endregion

        #region TheControllerShipsItsTableAndItsDirection()

        /// <summary>
        /// The node's policy is given the controller's table, the direction of
        /// a station and the controller's roles - and every permission of the
        /// table is one of a resource the controller has.
        /// </summary>
        [Test]
        public void TheControllerShipsItsTableAndItsDirection()
        {

            controller = TestControllers.New(directory, TestControllers.Offline);

            var policy     = controller.Node.OCPP.SignaturePolicy;
            var resources  = controller.Access.Resources;

            var unknown    = policy.ActionPermissions.
                                    SelectMany(permission => permission.Permissions).
                                    Where     (permission => !Permission.TryParse(permission, out var parsed, out _) ||
                                                             !resources.Contains(parsed.Resource)).
                                    ToArray   ();

            Assert.Multiple(() => {
                Assert.That(policy.IncomingDirection,                  Is.EqualTo("CSMS->CS"));
                Assert.That(policy.PermissionsOfRole,                  Is.Not.Null);
                Assert.That(policy.ActionPermissions.Select(permission => permission.Action),
                            Is.SupersetOf(new[] { "Reset", "TriggerMessage", "InstallCertificate", "DeleteCertificate", "CertificateSigned",
                                                  "SetNetworkProfile", "GetLog", "UpdateFirmware", "InstallSSHKey" }));
                Assert.That(unknown,                                   Is.Empty, "the table asks for a resource this controller does not have");
            });

        }

        #endregion

        #region WhatTheForwardingPassesIsNotHeldHere()

        /// <summary>
        /// What the controller forwards is held by the station it goes to: the
        /// forwarding's policy is another one, and is given no table here.
        /// </summary>
        [Test]
        public void WhatTheForwardingPassesIsNotHeldHere()
        {

            controller = TestControllers.New(directory, TestControllers.Offline);

            var own         = controller.Node.OCPP.SignaturePolicy;
            var forwarding  = controller.Node.OCPP.FORWARD.ForwardingSignaturePolicy;

            Assert.Multiple(() => {
                Assert.That(forwarding,                    Is.Not.SameAs(own));
                Assert.That(forwarding.ActionPermissions,  Is.Empty);
                Assert.That(forwarding.PermissionsOfRole,  Is.Null);
                Assert.That(forwarding.Mode,               Is.EqualTo(MessagePolicyModes.Off));
            });

        }

        #endregion

        #region ARestartIsJudgedByItsType()

        /// <summary>
        /// The support restarts the controller once it is idle, and not at once,
        /// which cuts every station off from the CSMS: that is the CPO's.
        /// </summary>
        [Test]
        public void ARestartIsJudgedByItsType()
        {

            var policy = AController(MessagePolicyModes.Enforce);

            Assert.Multiple(() => {

                Assert.That(AcceptsAReset(policy, OCPPv2_1.ResetType.OnIdle,     out var e1, support),  Is.True,  e1);
                Assert.That(AcceptsAReset(policy, OCPPv2_1.ResetType.Immediate,  out var e2, support),  Is.False, "The support restarted the controller at once!");
                Assert.That(e2,                                                                          Does.Contain("reset-immediate:run"));
                Assert.That(AcceptsAReset(policy, OCPPv2_1.ResetType.Immediate,  out var e3, cpo),      Is.True,  e3);
                Assert.That(AcceptsAReset(policy, OCPPv2_1.ResetType.OnIdle,     out var e4),           Is.False, "An unsigned restart got past the policy!");
                Assert.That(AcceptsAReset(policy, OCPPv2_1.ResetType.OnIdle,     out var e5, stranger), Is.False, "A role the controller does not know restarted it!");

            });

        }

        #endregion

        #region TheCertificatesAreTheAdministratorsAlone()

        /// <summary>
        /// The CPO runs the site but does not touch the certificates, here as on
        /// the web page: a deletion it signs is refused, the administrators' is
        /// not. A new certificate asked for by TriggerMessage needs the same.
        /// </summary>
        [Test]
        public void TheCertificatesAreTheAdministratorsAlone()
        {

            var policy = AController(MessagePolicyModes.Enforce);

            Assert.Multiple(() => {

                Assert.That(AcceptsACertificateDeletion(policy, out var e1, cpo),    Is.False, "The CPO deleted a certificate!");
                Assert.That(e1,                                                      Does.Contain("certificates:edit"));
                Assert.That(AcceptsACertificateDeletion(policy, out var e2, admin),  Is.True,  e2);

                Assert.That(AcceptsATrigger(policy, OCPPv2_1.MessageTrigger.SignChargingStationCertificate, out var e3, cpo),    Is.False, "The CPO had a new certificate made!");
                Assert.That(AcceptsATrigger(policy, OCPPv2_1.MessageTrigger.SignChargingStationCertificate, out var e4, admin),  Is.True,  e4);

                Assert.That(AcceptsATrigger(policy, OCPPv2_1.MessageTrigger.BootNotification,               out var e5, cpo),      Is.True,  e5);
                Assert.That(AcceptsATrigger(policy, OCPPv2_1.MessageTrigger.BootNotification,               out var e6, support),  Is.False, "The support had the controller send a message!");
                Assert.That(e6,                                                                                                     Does.Contain("node:run"));

            });

        }

        #endregion

        #region ARoleOfTheConfigurationFileSignsWhatItMayDo()

        /// <summary>
        /// The roles are the controller's, as its configuration file leaves
        /// them: a role the file adds may sign what the file says it may do,
        /// and nothing more.
        /// </summary>
        [Test]
        public void ARoleOfTheConfigurationFileSignsWhatItMayDo()
        {

            var policy = AController(MessagePolicyModes.Enforce);

            Assert.Multiple(() => {
                Assert.That(AcceptsAReset(policy, OCPPv2_1.ResetType.OnIdle,     out var e1, resetter),  Is.True,  e1);
                Assert.That(AcceptsAReset(policy, OCPPv2_1.ResetType.Immediate,  out var e2, resetter),  Is.False, "A role that may restart once idle restarted at once!");
            });

        }

        #endregion

        #region TheDeviceModelIsDecidedEntryByEntry()

        /// <summary>
        /// SetVariables and GetVariables have no row of the table: a request of a
        /// role that may change the name servers and nothing else passes as a
        /// whole, and its device model decides each entry by the resource of its
        /// component - the name servers "dns", the time "nts", the keys of the
        /// station port "certificates", the line up to the CSMS "csms", and
        /// anything else "configuration".
        /// </summary>
        [Test]
        public void TheDeviceModelIsDecidedEntryByEntry()
        {

            var policy = AController(MessagePolicyModes.Enforce);

            var passes      = AcceptsASetVariables(policy, out var error,     out var byTheNameServers, dns);
            var unsigned    = AcceptsASetVariables(policy, out _,             out var byNobody);
            var byTheAdmin  = AcceptsASetVariables(policy, out var adminError, out var byTheAdministrator, admin);

            Assert.Multiple(() => {

                Assert.That(policy.ActionPermissions.Select(permission => permission.Action),
                            Has.None.EqualTo("SetVariables").And.None.EqualTo("GetVariables"),   "a row would decide the request as a whole");
                Assert.That(policy.ActionsDecidedPerEntry,                Is.SupersetOf(new[] { "SetVariables", "GetVariables" }));

                Assert.That(passes,                                       Is.True,  error);
                Assert.That(byTheAdmin,                                   Is.True,  adminError);

                Assert.That(Entry(policy, byTheNameServers, "DNSClientController",       "Servers").        Applied, Is.True);
                Assert.That(Entry(policy, byTheNameServers, "DNSServerController",       "Address", Instance: "ns1").Applied, Is.True);
                Assert.That(Entry(policy, byTheNameServers, "DNSClientController",       "Servers", "read").Applied, Is.True);

                Assert.That(Entry(policy, byTheNameServers, "NTPController",             "ClientGroups").   Reason ?? "", Does.Contain("nts:edit"));
                Assert.That(Entry(policy, byTheNameServers, "NTPClientGroupController",  "Clients").        Reason ?? "", Does.Contain("nts:edit"));
                Assert.That(Entry(policy, byTheNameServers, "ClockCtrlr",                "TimeSource").     Reason ?? "", Does.Contain("nts:edit"));
                Assert.That(Entry(policy, byTheNameServers, "EVQICtrlr",                 "ActiveKeyPin", Instance: "TLSServer").Reason ?? "",
                                                                                                                   Does.Contain("certificates:edit"));
                Assert.That(Entry(policy, byTheNameServers, "EVQICtrlr",                 "KeyPins", "read", "TLSServer").Reason ?? "",
                                                                                                                   Does.Contain("certificates:read"));
                Assert.That(Entry(policy, byTheNameServers, "TLSClientController",       "TrustOnFirstUse", Instance: "csms").Reason ?? "",
                                                                                                                   Does.Contain("csms:edit"));

                // A pin set is the signers of pin sets' to hold it to, not a role's:
                // the policy lets it on to the line's own check, whoever signed it.
                Assert.That(Entry(policy, byTheNameServers, "TLSClientController",       "ServerKeyPins",        Instance: "csms").Applied, Is.True);
                Assert.That(Entry(policy, byTheNameServers, "TLSClientController",       "ServerKeyPinsVersion", Instance: "csms").Applied, Is.True);
                Assert.That(Entry(policy, byTheNameServers, "SecurityCtrlr",             "OrganizationName").Reason ?? "", Does.Contain("configuration:edit"));

                Assert.That(Entry(policy, byNobody,         "DNSClientController",       "Servers").        Applied, Is.False, "an unsigned entry was applied");

                Assert.That(Entry(policy, byTheAdministrator, "EVQICtrlr",               "ActiveKeyPin", Instance: "TLSServer").Applied, Is.True);
                Assert.That(Entry(policy, byTheAdministrator, "NTPController",           "ClientGroups").   Applied, Is.True);

            });

        }

        #endregion

    }

}
