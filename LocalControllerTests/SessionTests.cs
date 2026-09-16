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

using NUnit.Framework;

using cloud.charging.open.LocalController.Web;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// How long a browser stays signed in, and what ends it sooner.
    /// </summary>
    /// <remarks>
    /// All of it on a clock these tests move, because every span here is
    /// measured in hours and days. Sessions expire when they are looked at
    /// rather than when something fires, so moving the clock is enough - no
    /// timer has to run.
    /// </remarks>
    public class SessionTests
    {

        #region Data

        private const String ThePassword = "a-password-nobody-guesses";

        #endregion

        #region (private static) ASessionStore(Clock, IdleTimeout = null, MaximumLifetime = null)

        private static WebSessions ASessionStore(TestClock  Clock,
                                                 TimeSpan?  IdleTimeout       = null,
                                                 TimeSpan?  MaximumLifetime   = null)
        {

            WebLoginSettings.TryCreate("root", ThePassword, null, out var login, out var error);

            Assert.That(login, Is.Not.Null, error);

            return new WebSessions(
                       login,
                       IdleTimeout:      IdleTimeout,
                       MaximumLifetime:  MaximumLifetime,
                       TimeProvider:     Clock
                   );

        }

        #endregion


        #region TheRightPasswordStartsASession()

        [Test]
        public void TheRightPasswordStartsASession()
        {

            var sessions = ASessionStore(TestClock.At(2026, 1, 1));

            Assert.Multiple(() => {
                Assert.That(sessions.TryLogin("root", ThePassword, out var session), Is.True);
                Assert.That(session,                                                 Is.Not.Null);
                Assert.That(sessions.Count,                                          Is.EqualTo(1));
            });

        }

        #endregion

        #region AWrongPasswordOrUsernameStartsNothing()

        [Test]
        public void AWrongPasswordOrUsernameStartsNothing()
        {

            var sessions = ASessionStore(TestClock.At(2026, 1, 1));

            Assert.Multiple(() => {
                Assert.That(sessions.TryLogin("root",     "wrong",      out _), Is.False);
                Assert.That(sessions.TryLogin("somebody", ThePassword,  out _), Is.False);
                Assert.That(sessions.TryLogin(null,       null,         out _), Is.False);
                Assert.That(sessions.Count, Is.EqualTo(0));
            });

        }

        #endregion

        #region ASessionNobodyUsesEndsAtTheIdleTimeout()

        /// <summary>
        /// The twelve hours are counted from the last request, not from the
        /// sign-in - which is what makes them an idle timeout rather than a
        /// lifetime.
        /// </summary>
        [Test]
        public void ASessionNobodyUsesEndsAtTheIdleTimeout()
        {

            var clock    = TestClock.At(2026, 1, 1);
            var sessions = ASessionStore(clock, IdleTimeout: TimeSpan.FromHours(12));

            sessions.TryLogin("root", ThePassword, out _);

            clock.Advance(TimeSpan.FromHours(11));
            Assert.That(sessions.Count, Is.EqualTo(1), "The session ended before its idle timeout.");

            clock.Advance(TimeSpan.FromHours(2));
            Assert.That(sessions.Count, Is.EqualTo(0), "The session outlived its idle timeout.");

        }

        #endregion

        #region AMaximumLifetimeEndsASessionHoweverBusyItIs()

        /// <summary>
        /// The other end of it: a browser that keeps asking cannot keep a
        /// session for ever, because a password change has to reach it
        /// eventually even if nobody signs out.
        /// </summary>
        [Test]
        public void AMaximumLifetimeEndsASessionHoweverBusyItIs()
        {

            var clock    = TestClock.At(2026, 1, 1);

            var sessions = ASessionStore(clock,
                                         IdleTimeout:      TimeSpan.FromHours(12),
                                         MaximumLifetime:  TimeSpan.FromDays(2));

            sessions.TryLogin("root", ThePassword, out var session);

            // Used every six hours for three days: never idle, always within
            // the idle timeout, and still over in two days.
            for (var i = 0; i < 12; i++)
            {
                clock.Advance(TimeSpan.FromHours(6));
                sessions.Store.TryGet(session!.Token, out _);
            }

            Assert.That(sessions.Count, Is.EqualTo(0),
                        "A session that was used constantly outlived its maximum lifetime.");

        }

        #endregion

        #region UsingASessionPutsOffItsIdleTimeout()

        [Test]
        public void UsingASessionPutsOffItsIdleTimeout()
        {

            var clock    = TestClock.At(2026, 1, 1);
            var sessions = ASessionStore(clock, IdleTimeout: TimeSpan.FromHours(12));

            sessions.TryLogin("root", ThePassword, out var session);

            clock.Advance(TimeSpan.FromHours(11));

            Assert.That(sessions.Store.TryGet(session!.Token, out _), Is.True,
                        "The session was gone before its timeout.");

            // Eleven more hours: past the original timeout, but the request
            // above moved it.
            clock.Advance(TimeSpan.FromHours(11));

            Assert.That(sessions.Count, Is.EqualTo(1),
                        "Using a session did not put off its idle timeout.");

        }

        #endregion

        #region ChangingTheLoginEndsEveryOtherSession()

        /// <summary>
        /// Changing the password has to end the other sessions, or it does not
        /// do what whoever changed it thinks it does: a browser signed in with
        /// the old password would keep the page for as long as its cookie
        /// lives. The one making the change is kept, so nobody signs themselves
        /// out.
        /// </summary>
        [Test]
        public void ChangingTheLoginEndsEveryOtherSession()
        {

            var clock    = TestClock.At(2026, 1, 1);
            var sessions = ASessionStore(clock);

            sessions.TryLogin("root", ThePassword, out var theOneChangingIt);
            sessions.TryLogin("root", ThePassword, out _);
            sessions.TryLogin("root", ThePassword, out _);

            Assert.That(sessions.Count, Is.EqualTo(3));

            WebLoginSettings.TryCreate("root", "a-completely-different-one", null, out var newLogin, out _);

            var ended = sessions.UpdateLogin(newLogin!, theOneChangingIt!.Token);

            Assert.Multiple(() => {
                Assert.That(ended,                                                  Is.EqualTo(2));
                Assert.That(sessions.Count,                                         Is.EqualTo(1));
                Assert.That(sessions.Store.TryGet(theOneChangingIt.Token, out _),   Is.True,
                            "The session that changed the password was signed out by its own change.");
                Assert.That(sessions.TryLogin("root", ThePassword,               out _), Is.False,
                            "The old password still works.");
                Assert.That(sessions.TryLogin("root", "a-completely-different-one", out _), Is.True);
            });

        }

        #endregion

        #region TheCookieSaysWhatACookieOfThisKindMustSay()

        /// <summary>
        /// HttpOnly so that script cannot read it, SameSite=strict so that it
        /// does not travel to another site's request, and Path=/ so that it
        /// covers the whole interface.
        /// </summary>
        [Test]
        public void TheCookieSaysWhatACookieOfThisKindMustSay()
        {

            var clock    = TestClock.At(2026, 1, 1);
            var sessions = ASessionStore(clock);

            sessions.TryLogin("root", ThePassword, out var session);

            var cookie = sessions.SessionCookie(session!).ToString();

            Assert.Multiple(() => {
                Assert.That(cookie, Does.Contain("HttpOnly"));
                Assert.That(cookie, Does.Contain("SameSite=strict"));
                Assert.That(cookie, Does.Contain("Path=/"));
                Assert.That(cookie, Does.Contain(session.Token.ToString()));
            });

        }

        #endregion

        #region SigningOutExpiresTheCookieIn1970()

        /// <summary>
        /// The browser is told to drop it, rather than merely not being given
        /// a new one.
        /// </summary>
        [Test]
        public void SigningOutExpiresTheCookieIn1970()
        {

            var sessions = ASessionStore(TestClock.At(2026, 1, 1));

            var expired  = sessions.ExpiredCookie().ToString();

            Assert.Multiple(() => {
                Assert.That(expired, Does.Contain("1970"));
                Assert.That(expired, Does.Contain("HttpOnly"));
            });

        }

        #endregion

    }

}
