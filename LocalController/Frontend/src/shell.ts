import { auth } from './auth';
import { config } from './config';
import { html, must, render, type HTMLFragment } from './html';

/**
 * The frame every signed-in page sits in: the menu on the left, a heading and
 * whatever the page puts under it on the right.
 *
 * The menu is here and not in main.ts because each page renders itself into an
 * emptied outlet - so the frame is drawn again with every navigation, and the
 * entry that is current is simply the one that says so. One place decides what
 * the local controller has pages for.
 */

export interface MenuEntry {
    path:      string;
    label:     string;
    /** A Font Awesome class, e.g. "fa-sliders". */
    icon:      string;
    /** The pages below this one, shown indented while one of them is open. */
    children?: MenuEntry[];
}

/** What the local controller can show. */
export const menu: MenuEntry[] = [
    {
        path:      '/configuration',
        label:     'Configuration',
        icon:      'fa-sliders',
        children:  [
            { path: '/configuration/dns',  label: 'DNS client',  icon: 'fa-magnifying-glass-location' },
            { path: '/configuration/nts',  label: 'NTS client',  icon: 'fa-clock'                     }
        ]
    },
    { path: '/logs', label: 'Logs', icon: 'fa-list-ul' }
];

/** Every entry of the menu, parents and children alike. */
export function allMenuEntries(): MenuEntry[] {
    return menu.flatMap(entry => [entry, ...(entry.children ?? [])]);
}


export interface ShellOptions {
    /** The menu entry to mark as the current one. */
    active:     string;
    /** The heading of the page. */
    title:      string;
    /** One line under the heading, or nothing. */
    subtitle?:  string;
    /** Buttons and such, shown at the right of the heading. */
    actions?:   HTMLFragment;
}


/**
 * Draw the frame into the given root and hand back the element the page is to
 * render itself into.
 */
export function shell(root:     HTMLElement,
                      options:  ShellOptions): HTMLElement {

    render(root, html`
        <div class="shell">

            <nav class="sidebar" aria-label="Sections">

                <div class="brand">
                    <i class="fa-solid fa-sitemap"></i>
                    <span>Local Controller</span>
                </div>

                <ul class="menu">
                    ${menu.map(entry => html`
                        <li>
                            ${link(entry, options.active)}
                            ${entry.children && isOpen(entry, options.active)
                                  ? html`
                                      <ul class="submenu">
                                          ${entry.children.map(child => html`<li>${link(child, options.active)}</li>`)}
                                      </ul>
                                  `
                                  : ''}
                        </li>
                    `)}
                </ul>

                <div class="sidebar-foot">
                    <div class="who" title="Signed in as ${auth.user?.roles?.join(', ') ?? 'nobody'}">
                        <i class="fa-solid fa-user"></i>
                        <span>${auth.user?.username ?? '-'}</span>
                    </div>
                    ${auth.user?.roles?.length
                          ? html`<div class="roles small muted">${auth.user.roles.join(', ')}</div>`
                          : ''}
                    <button type="button" id="sign-out" class="btn small">Sign out</button>
                    <div class="versions small muted">
                        controller ${config.serverVersion} &middot; web ${config.frontendVersion}
                    </div>
                </div>

            </nav>

            <main class="content">

                <header class="page-head">
                    <div>
                        <h1>${options.title}</h1>
                        ${options.subtitle ? html`<p class="muted">${options.subtitle}</p>` : ''}
                    </div>
                    <div class="page-actions">${options.actions ?? ''}</div>
                </header>

                <div id="content-body" class="content-body"></div>

            </main>

        </div>
    `);

    must<HTMLButtonElement>(root, '#sign-out').
        addEventListener('click', () => void auth.signOut());

    return must<HTMLElement>(root, '#content-body');

}


/** One entry of the menu, marked when it is the page being shown. */
function link(entry: MenuEntry, active: string): HTMLFragment {

    const current = entry.path === active;

    return html`
        <a href="${entry.path}"
           class="${current ? 'active' : ''}"
           ${current ? html`aria-current="page"` : ''}>
            <i class="fa-solid ${entry.icon}"></i>
            <span>${entry.label}</span>
        </a>
    `;

}

/**
 * Whether an entry's children are shown: while the entry itself is open, or
 * while one of them is. The menu unfolds where somebody is and stays folded
 * everywhere else, so a controller with a dozen pages still fits on the left.
 */
function isOpen(entry: MenuEntry, active: string): boolean {
    return entry.path === active ||
           (entry.children ?? []).some(child => child.path === active);
}
