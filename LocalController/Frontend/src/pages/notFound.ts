import { auth } from '../auth';
import { html, render } from '../html';
import type { Page } from '../router';
import { menu, shell } from '../shell';

export const notFoundPage: Page = {

    title: 'Not found',

    render({ root, url }) {

        // Signed out, this page is all there is - no menu to put it in.
        const content = auth.user
                            ? shell(root, { active: '', title: 'Not found' })
                            : root;

        render(content, html`
            <section class="not-found">
                <p>There is no page at <code>${url.pathname}</code>.</p>
                <p class="muted">
                    This local controller has
                    ${menu.map((entry, index) => html`${index > 0 ? ' and ' : ''}<a href="${entry.path}">${entry.label}</a>`)}.
                </p>
            </section>
        `);

    }

};
