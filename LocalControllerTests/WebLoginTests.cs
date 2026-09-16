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

using cloud.charging.open.LocalController.Web;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The one login a local controller has: how it is made, what it grants,
    /// and what the file it lives in may and may not contain.
    /// </summary>
    public class WebLoginTests
    {

        #region Data

        private String directory = default!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
            => directory = TestControllers.TemporaryDirectory("login");

        [TearDown]
        public void RemoveTheDirectory()
            => TestControllers.Remove(directory);

        #endregion


        #region APasswordIsNeverKeptInTheClear()

        /// <summary>
        /// Not in the object, and not in what it prints.
        /// </summary>
        [Test]
        public void APasswordIsNeverKeptInTheClear()
        {

            const String password = "the-password-itself";

            WebLoginSettings.TryCreate("root", password, null, out var login, out var error);

            Assert.That(login, Is.Not.Null, error);

            Assert.Multiple(() => {
                Assert.That(login!.ToString(),                             Does.Not.Contain(password));
                Assert.That(login.Password.ToString(),                     Does.Not.Contain(password));
                Assert.That(login.ToJSON(IncludePasswordHash: true).ToString(), Does.Not.Contain(password));
                // What it is kept as instead.
                Assert.That(login.Password.ToString(),                     Does.StartWith("$pbkdf2"));
            });

        }

        #endregion

        #region AShortPasswordIsRefused()

        [Test]
        public void AShortPasswordIsRefused()
        {

            var ok = WebLoginSettings.TryCreate("root", "short", null, out var login, out var error);

            Assert.Multiple(() => {
                Assert.That(ok,     Is.False);
                Assert.That(login,  Is.Null);
                Assert.That(error,  Does.Contain($"{WebLoginSettings.MinimumPasswordLength}"));
            });

        }

        #endregion

        #region ALoginWithoutAUsernameIsRefused()

        [Test]
        public void ALoginWithoutAUsernameIsRefused()
        {

            Assert.Multiple(() => {
                Assert.That(WebLoginSettings.TryCreate("",    "a-long-enough-password", null, out _, out _), Is.False);
                Assert.That(WebLoginSettings.TryCreate("   ", "a-long-enough-password", null, out _, out _), Is.False);
                Assert.That(WebLoginSettings.TryCreate(null,  "a-long-enough-password", null, out _, out _), Is.False);
            });

        }

        #endregion

        #region AGeneratedPasswordIsLongAndOpensTheController()

        /// <summary>
        /// What a first start makes up for itself, and hands back exactly once.
        /// </summary>
        [Test]
        public void AGeneratedPasswordIsLongAndOpensTheController()
        {

            var (login, password) = WebLoginSettings.Generate();

            Assert.Multiple(() => {
                Assert.That(login.Username,               Is.EqualTo(WebLoginSettings.DefaultUsername));
                Assert.That(password,                     Has.Length.GreaterThanOrEqualTo(20));
                Assert.That(login.Verify("root", password), Is.True);
                Assert.That(login.Roles,                  Is.EquivalentTo(new[] { UserRole.SystemAdmin }));
            });

        }

        #endregion

        #region VerifyIsWrongForTheWrongHalfOfThePair()

        /// <summary>
        /// Both halves have to be right, and a wrong username is refused just
        /// as a wrong password is.
        /// </summary>
        [Test]
        public void VerifyIsWrongForTheWrongHalfOfThePair()
        {

            WebLoginSettings.TryCreate("root", "a-long-enough-password", null, out var login, out _);

            Assert.Multiple(() => {
                Assert.That(login!.Verify("root",     "a-long-enough-password"), Is.True);
                Assert.That(login.Verify ("root",     "something-else"),         Is.False);
                Assert.That(login.Verify ("somebody", "a-long-enough-password"), Is.False);
                Assert.That(login.Verify (null,       null),                     Is.False);
            });

        }

        #endregion


        #region AFileWithoutRolesDescribesASystemAdministrator()

        /// <summary>
        /// That is what the one login of a controller used to be, and reading
        /// an old file as something less would lock whoever wrote it out of
        /// their own controller.
        /// </summary>
        [Test]
        public void AFileWithoutRolesDescribesASystemAdministrator()
        {

            WebLoginSettings.TryCreate("root", "a-long-enough-password", null, out var made, out _);

            var written = made!.ToJSON(IncludePasswordHash: true);
            written.Remove("roles");

            var ok = WebLoginSettings.TryParse(written, out var read, out var error);

            Assert.Multiple(() => {
                Assert.That(ok,           Is.True, error);
                Assert.That(read!.Roles,  Is.EquivalentTo(new[] { UserRole.SystemAdmin }));
            });

        }

        #endregion

        #region AFileWithAnEmptyRoleListIsRefused()

        /// <summary>
        /// Somebody wrote a list and meant something by it, and "may sign in
        /// and do nothing" is not a thing anybody means.
        /// </summary>
        [Test]
        public void AFileWithAnEmptyRoleListIsRefused()
        {

            WebLoginSettings.TryCreate("root", "a-long-enough-password", null, out var made, out _);

            var written = made!.ToJSON(IncludePasswordHash: true);
            written["roles"] = new JArray();

            Assert.That(WebLoginSettings.TryParse(written, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("roles"));

        }

        #endregion

        #region AnUnknownRoleIsRefusedRatherThanIgnored()

        /// <summary>
        /// A role this controller has never heard of is one it cannot enforce,
        /// so the file is refused rather than quietly granting nothing - or,
        /// worse, being taken for a known one because it looks similar.
        /// </summary>
        [Test]
        public void AnUnknownRoleIsRefusedRatherThanIgnored()
        {

            WebLoginSettings.TryCreate("root", "a-long-enough-password", null, out var made, out _);

            var written = made!.ToJSON(IncludePasswordHash: true);
            written["roles"] = new JArray("supervisor");

            Assert.That(WebLoginSettings.TryParse(written, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("supervisor"));

        }

        #endregion

        #region TheFileSurvivesARoundTrip()

        [Test]
        public void TheFileSurvivesARoundTrip()
        {

            var path = Path.Combine(directory, "web-login.json");
            var file = new WebLoginFile(path);

            Assert.That(file.Exists, Is.False, "There was a file before anything wrote one.");

            WebLoginSettings.TryCreate("operator", "a-long-enough-password", [ UserRole.CPO ], out var login, out _);

            file.Save(login!);

            var second = new WebLoginFile(path);

            Assert.Multiple(() => {
                Assert.That(second.Exists,                                     Is.True);
                Assert.That(second.TryLoad(out var read, out var error),       Is.True, error);
                Assert.That(read!.Username,                                    Is.EqualTo("operator"));
                Assert.That(read.Roles,                                        Is.EquivalentTo(new[] { UserRole.CPO }));
                Assert.That(read.Verify("operator", "a-long-enough-password"), Is.True,
                            "The password did not survive being written and read back.");
            });

        }

        #endregion

        #region AnUnreadableFileIsAnErrorAndNotAnAbsentOne()

        /// <summary>
        /// No file at all is a first start. A file that is there and cannot be
        /// read is somebody's login that something has happened to, and saying
        /// so is the only safe answer - making up a new password instead would
        /// lock them out without a word.
        /// </summary>
        [Test]
        public void AnUnreadableFileIsAnErrorAndNotAnAbsentOne()
        {

            var path = Path.Combine(directory, "web-login.json");

            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{ this is not json");

            var file = new WebLoginFile(path);

            Assert.Multiple(() => {
                Assert.That(file.TryLoad(out var login, out var error), Is.False);
                Assert.That(login,                                      Is.Null);
                Assert.That(error,                                      Is.Not.Null,
                            "An unreadable login file came back as merely absent.");
            });

        }

        #endregion


        #region EveryRoleGrantsSomethingAndTheAdminGrantsEverything()

        [Test]
        public void EveryRoleGrantsSomethingAndTheAdminGrantsEverything()
        {

            var everything = Enum.GetValues<Permissions>().
                                  Where(permission => permission != Permissions.None).
                                  Aggregate(Permissions.None, (all, one) => all | one);

            Assert.Multiple(() => {

                foreach (var role in UserRole.All)
                    Assert.That(role.Permissions, Is.Not.EqualTo(Permissions.None),
                                $"The {role.Name} role grants nothing at all.");

                Assert.That(UserRole.SystemAdmin.Permissions, Is.EqualTo(everything),
                            "The system administrator does not grant everything this controller knows.");

                // A viewer may look and do nothing, which is the whole of it.
                Assert.That(UserRole.Viewer.Permissions, Is.EqualTo(Permissions.ReadConfiguration));

            });

        }

        #endregion

        #region RolesAddUpAndAreNamedAsTheBrowserReadsThem()

        [Test]
        public void RolesAddUpAndAreNamedAsTheBrowserReadsThem()
        {

            var together = new[] { UserRole.Viewer, UserRole.CPO }.PermissionsOf();

            Assert.Multiple(() => {

                Assert.That(together.HasFlag(Permissions.ReadConfiguration),     Is.True);
                Assert.That(together.HasFlag(Permissions.ChangeNetworkSettings), Is.True);

                // camelCase, because that is how the bundle spells them.
                Assert.That(Permissions.ReadConfiguration.Names(), Is.EquivalentTo(new[] { "readConfiguration" }));
                Assert.That(Permissions.None.Names(),              Is.Empty);

            });

        }

        #endregion

        #region ARoleIsFoundByItsNameInAnyCase()

        [Test]
        public void ARoleIsFoundByItsNameInAnyCase()
        {

            Assert.Multiple(() => {

                Assert.That(UserRole.TryParse("cpo",         out var lower, out _), Is.True);
                Assert.That(lower,                                                  Is.EqualTo(UserRole.CPO));

                Assert.That(UserRole.TryParse("SystemAdmin", out var mixed, out _), Is.True);
                Assert.That(mixed,                                                  Is.EqualTo(UserRole.SystemAdmin));

                Assert.That(UserRole.TryParse("  viewer  ",  out var padded, out _), Is.True);
                Assert.That(padded,                                                  Is.EqualTo(UserRole.Viewer));

                Assert.That(UserRole.TryParse("installer",   out _, out var error),  Is.False,
                            "A role this controller does not have was accepted.");
                Assert.That(error,                                                   Does.Contain("installer"));

            });

        }

        #endregion

    }

}
