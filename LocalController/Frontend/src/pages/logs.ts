import { logLevels, type LogEntry, type LogLevel } from '../api/client';
import { escapeHTML, html, must, render } from '../html';
import { logs } from '../logs/store';
import type { Page } from '../router';
import { shell } from '../shell';
import { formatTime, formatTimestamp, isAtLeast } from '../ui';

/**
 * Everything that happens inside the local controller, as it happens.
 *
 * The entries arrive over one Server-Sent Events stream and go in at the top,
 * newest first, so the line worth reading is the one that is already on screen;
 * the filters work on what is already in the browser, so changing one costs
 * nothing and asks the local controller for nothing. A list that is scrolled
 * to the top follows along; scrolling down stops that, which is what somebody
 * reading an older line wants - and the button at the bottom brings them back.
 */
export const logsPage: Page = {

    title: 'Logs',

    render({ root }) {

        const content = shell(root, {
            active:    '/logs',
            title:     'Logs',
            subtitle:  'Everything this local controller does, as it happens.',
            actions:   html`
                <span id="stream-state" class="stream-state"></span>
                <button type="button" id="clear" class="btn small" title="Clear what this page shows; the local controller keeps its log">Clear view</button>
            `
        });

        render(content, html`

            <div class="log-filters">

                <label class="filter-search">
                    <i class="fa-solid fa-magnifying-glass"></i>
                    <input type="search" id="search" placeholder="Search the messages ..." autocomplete="off" />
                </label>

                <label class="filter-level">
                    Level
                    <select id="level">
                        ${logLevels.map(level => html`
                            <option value="${level}" ${level === 'debug' ? html`selected` : ''}>${level}</option>
                        `)}
                    </select>
                </label>

                <label class="filter-follow">
                    <input type="checkbox" id="follow" checked />
                    Follow
                </label>

            </div>

            <div id="tags" class="tag-filters"></div>

            <div class="log-pane">

                <button type="button" id="to-top" class="btn small jump-newest" hidden>
                    <i class="fa-solid fa-arrow-up"></i>
                    Jump to the newest
                </button>

                <div id="log" class="log" role="log" aria-live="polite" tabindex="0"></div>

            </div>

            <div class="log-foot small muted">
                <span id="counts"></span>
            </div>

        `);

        const list        = must<HTMLElement>       (content, '#log');
        const tagBox      = must<HTMLElement>       (content, '#tags');
        const counts      = must<HTMLElement>       (content, '#counts');
        const toTop       = must<HTMLButtonElement> (content, '#to-top');
        const search      = must<HTMLInputElement>  (content, '#search');
        const level       = must<HTMLSelectElement> (content, '#level');
        const follow      = must<HTMLInputElement>  (content, '#follow');
        const streamState = must<HTMLElement>       (root,    '#stream-state');
        const clear       = must<HTMLButtonElement> (root,    '#clear');

        /** The tags somebody has switched on; empty means "every tag". */
        const chosenTags = new Set<string>();

        let renderedTags = '';

        /**
         * What the last correction still owes the view.
         *
         * scrollTop snaps to whole device pixels, so asking for 24.32 px on a
         * screen of one and a half sets 24 and drops the rest. Every line of a
         * log is the same height, so the same fraction is dropped every time -
         * this is not noise that cancels itself out but a drift in one
         * direction, a third of a pixel a line, a screenful over a busy
         * evening. Carried here and added to the next correction, where the
         * browser can finally take it.
         */
        let scrollDebt = 0;


        function matches(entry: LogEntry): boolean {

            if (!isAtLeast(entry.level, level.value as LogLevel))
                return false;

            if (chosenTags.size > 0) {

                // Any of the chosen ones, not all of them: somebody who picks
                // "ocpp" and "15118" wants to watch both conversations, not
                // the empty set of lines that are about both at once. The
                // level counts as a tag, which is how "critical" and "ocpp"
                // can be picked together.
                const own = new Set<string>([entry.level, ...entry.tags]);

                let hit = false;

                for (const tag of chosenTags) {
                    if (own.has(tag)) {
                        hit = true;
                        break;
                    }
                }

                if (!hit)
                    return false;

            }

            const needle = search.value.trim().toLowerCase();

            return needle.length === 0 ||
                   entry.message.toLowerCase().includes(needle);

        }


        function lineHTML(entry: LogEntry): string {
            return `<div class="line ${entry.level}" data-id="${entry.id}">` +
                       `<time datetime="${escapeHTML(entry.timestamp)}" title="${escapeHTML(formatTimestamp(entry.timestamp))}">${escapeHTML(formatTime(entry.timestamp))}</time>` +
                       `<span class="chip level ${entry.level}">${escapeHTML(entry.level)}</span>` +
                       entry.tags.map(tag => `<span class="chip tag">${escapeHTML(tag)}</span>`).join('') +
                       `<span class="message">${escapeHTML(entry.message)}</span>` +
                   `</div>`;
        }

        function atTop(): boolean {
            // A few pixels of slack: a list that is one rounding error short
            // of the top is, to the person reading it, at the top.
            return list.scrollTop <= 24;
        }

        function scrollToTop(): void {
            list.scrollTop = 0;
            scrollDebt     = 0;
            toTop.hidden   = true;
        }

        /** Everything again: after a reload, or when a filter changed. */
        function redraw(): void {

            // Everything is drawn again, so nothing is owed from before.
            scrollDebt = 0;

            // The store keeps its entries oldest first, because that is the
            // order their ids come in and the order the next batch continues;
            // only what is shown is turned around. Reversing the copy that
            // filter() just made, never the store itself.
            list.innerHTML = logs.entries.filter(matches).reverse().map(lineHTML).join('') ||
                             '<div class="log-empty">Nothing to show. The local controller has been quiet, or the filters are too narrow.</div>';

            if (follow.checked)
                scrollToTop();

            updateCounts();
            drawTags();

        }

        /** Only what is new: the usual case, and the cheap one. */
        function prepend(added: LogEntry[]): void {

            const wanted = added.filter(matches);

            if (wanted.length > 0) {

                const stick = follow.checked && atTop();

                list.querySelector('.log-empty')?.remove();

                // Whatever goes in above the line that is first right now
                // pushes that line down by its own height, so asking the line
                // how far it moved is asking how much was added - and asking
                // it this way answers in fractions of a pixel.
                //
                // The obvious way is the difference of two scrollHeights, and
                // that one is rounded to whole pixels. Half a pixel lost per
                // batch is invisible in any one of them and is still there
                // after the next thousand, which on a busy log is an hour.
                const anchor    = list.firstElementChild;
                const anchorTop = anchor?.getBoundingClientRect().top ?? 0;

                // Turned around inside the batch as well: a burst that arrives
                // in one event would otherwise sit at the top back to front.
                list.insertAdjacentHTML('afterbegin', wanted.reverse().map(lineHTML).join(''));

                // Read before the trimming below, which takes its lines off
                // the bottom - that moves nothing above it, but it can take
                // the anchor itself when the store has just wrapped.
                const grew = anchor
                                 ? anchor.getBoundingClientRect().top - anchorTop
                                 : 0;

                // The local controller keeps a bounded log and so does this page; what
                // fell out of the store has to leave the list as well - and that is
                // the oldest, which is now the last line rather than the first.
                while (list.childElementCount > logs.entries.length)
                    list.lastElementChild?.remove();

                if (stick)
                    scrollToTop();

                else {
                    // Lines going in above the viewport push everything below
                    // them down, so the older line somebody stopped to read
                    // would walk off the screen at the speed the log fills.
                    // Put the view back where it was, by exactly what was
                    // added and whatever the last correction was short.
                    const asked     = list.scrollTop + grew + scrollDebt;
                    list.scrollTop  = asked;

                    // What the browser took is not always what it was asked
                    // for. Only the snapping is worth carrying: when it
                    // refuses a larger jump than that - the list is at its end
                    // already, or the trimming above took the ground away -
                    // the difference is not a rounding error, and carrying it
                    // would be arguing with the browser rather than with the
                    // arithmetic.
                    const refused   = asked - list.scrollTop;
                    scrollDebt      = Math.abs(refused) < 1 ? refused : 0;

                    toTop.hidden    = false;
                }

            }

            updateCounts();
            drawTags();

        }

        function updateCounts(): void {

            const shown = list.querySelectorAll('.line').length;

            counts.textContent = `${shown} of ${logs.entries.length} entries` +
                                 (logs.capacity > 0 ? ` (the local controller keeps the last ${logs.capacity})` : '');

        }

        /** The tag buttons, redrawn only when the local controller has learned a new tag. */
        function drawTags(): void {

            const all = [...new Set([...logLevels, ...logs.tags])].sort();

            // NUL as the separator, written as an escape rather than as the
            // byte itself - the byte made this file binary to git, which shows
            // every change to it as a blob instead of a diff, and to grep,
            // which then skips it without a word. A tag may hold anything a
            // tag may hold, so the separator has to be the one thing it
            // cannot contain, or two different sets could share a key.
            const key = all.join('\0') + '|' + [...chosenTags].sort().join('\0');

            if (key === renderedTags)
                return;

            renderedTags = key;

            tagBox.innerHTML = all.map(tag =>
                `<button type="button" class="chip tag-button ${chosenTags.has(tag) ? 'on' : ''}" data-tag="${escapeHTML(tag)}" aria-pressed="${chosenTags.has(tag)}">${escapeHTML(tag)}</button>`
            ).join('') +
            (chosenTags.size > 0
                 ? '<button type="button" class="chip tag-button clear-tags" data-tag="">all tags</button>'
                 : '');

        }

        function showStream(): void {
            streamState.className   = `stream-state ${logs.streamConnected ? 'live' : 'down'}`;
            streamState.textContent = logs.streamConnected ? 'live' : 'reconnecting ...';
        }


        // Events

        tagBox.addEventListener('click', event => {

            const button = (event.target as Element | null)?.closest<HTMLElement>('.tag-button');

            if (!button)
                return;

            const tag = button.dataset.tag ?? '';

            if (tag === '')
                chosenTags.clear();
            else if (chosenTags.has(tag))
                chosenTags.delete(tag);
            else
                chosenTags.add(tag);

            renderedTags = '';
            redraw();

        });

        search  .addEventListener('input',  () => redraw());
        level   .addEventListener('change', () => redraw());
        follow  .addEventListener('change', () => { if (follow.checked) scrollToTop(); });
        toTop   .addEventListener('click',  () => scrollToTop());
        clear   .addEventListener('click',  () => logs.clear());

        list.addEventListener('scroll', () => {
            if (atTop())
                toTop.hidden = true;
        });

        const stopListening = logs.onChange(event => {

            switch (event.type) {

                case 'entries':
                    prepend(event.added);
                    break;

                case 'reloaded':
                    redraw();
                    break;

                case 'stream':
                    showStream();
                    break;

                case 'error':
                    list.insertAdjacentHTML('afterbegin', `<div class="line error"><span class="message">${escapeHTML(event.text)}</span></div>`);
                    break;

            }

        });

        showStream();
        redraw();

        // The store keeps running between pages - the log goes on filling while
        // somebody reads the configuration - so only this page's listener goes.
        return stopListening;

    }

};
