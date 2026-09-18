import { api } from '../api/client';
import { auth } from '../auth';
import { config } from '../config';
import { html, must, render } from '../html';
import type { Page } from '../router';
import { errorMessage, field, safeNext } from '../ui';

export const loginPage: Page = {

    title: 'Sign in',

    render({ root, url, navigate }) {

        const next = safeNext(url.searchParams.get('next')) ?? '/';

        if (auth.user) {
            navigate(next, true);
            return;
        }

        render(root, html`
            <section class="login">

                <h1><i class="fa-solid fa-sitemap"></i> Local Controller</h1>
                <p class="muted">Sign in to configure this local controller and read its log.</p>

                <form id="login-form" class="form-stack">
                    <label>Username
                        <input name="username" required autocomplete="username" autofocus />
                    </label>
                    <label>Password
                        <input name="password" type="password" required autocomplete="current-password" />
                    </label>
                    <div class="form-actions">
                        <button type="submit" class="btn primary">Sign in</button>
                        <span id="form-error" class="form-error" role="alert"></span>
                    </div>
                </form>

                <p class="small muted login-hint">
                    The local controller makes up one account at its first start
                    and shows its password once, on the console.
                </p>

                <p class="small muted">controller ${config.serverVersion} &middot; web ${config.frontendVersion}</p>

            </section>
        `);

        const form    = must<HTMLFormElement>(root, '#login-form');
        const error   = must<HTMLElement>(root, '#form-error');
        const button  = must<HTMLButtonElement>(form, 'button[type="submit"]');

        form.addEventListener('submit', event => {

            event.preventDefault();
            error.textContent = '';
            button.disabled   = true;

            void (async () => {
                try
                {
                    // The password is read untrimmed: a space at either end is
                    // part of it, and quietly dropping one would turn a right
                    // password into a wrong one.
                    auth.set(await api.auth.login(field(form, 'username'), field(form, 'password', false)));
                    navigate(next, true);
                }
                catch (problem)
                {
                    error.textContent = errorMessage(problem);
                    button.disabled   = false;
                }
            })();

        });

    }

};
