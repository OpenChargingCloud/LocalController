/**
 * The state drawn, in a document of happy-dom, against a stand-in controller:
 * the line up to the CSMS, the stations seen and the limits in force, and
 * again on Reload with what the controller says then; how its VTN is reached
 * through a connection - the transport, the binding of OpenADR, whether the
 * events are subscribed to, how many notifications came and when it was last
 * asked -, and nothing of it where the VTN is asked over HTTP.
 */

import { open, until, type Asked } from '../../test/controller.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

const { statePage } = await import('./state.ts');


let connected = true;

/** How the stand-in says its VTN is reached; null for over HTTP. */
let through: { transport: 'ws' | 'wss' | 'ssh'; binding: 'messages' | 'rest' | null; subscribed: boolean | null } | null = null;

/** How many notifications the stand-in says came. */
let notifications = 0;

function controller({ path }: Asked): unknown {

    if (path === '/state')
        return {
            csms:      { enabled: true, connected, connectedSince: connected ? '2026-11-12T08:00:00Z' : null },
            stations:  { connected: connected ? 1 : 0,
                         seen: [ { id: 'cs001', connected, over: connected ? 'SSH' : null,
                                   connectedSince: connected ? '2026-11-12T08:01:00Z' : null, lastSeen: '2026-11-12T08:02:00Z' },
                                 { id: 'cs002', connected: false, over: null, connectedSince: null, lastSeen: '2026-11-12T07:00:00Z' } ] },
            openADR:   { running: true, signedIn: true, lastPoll: '2026-11-12T08:00:00Z', lastRead: '2026-11-12T08:00:00Z',
                         transport: through?.transport ?? null, binding: through?.binding ?? null, subscribed: through?.subscribed ?? null,
                         notificationsReceived: notifications,
                         importKW: 40, exportKW: null,
                         limits: [ { eventID: 'e1', name: 'evening peak', kind: 'IMPORT_CAPACITY_LIMIT', kW: 40,
                                     start: '2026-11-12T07:00:00Z', end: null, state: 'active' },
                                   { eventID: 'e2', name: 'tomorrow', kind: 'IMPORT_CAPACITY_LIMIT', kW: 20,
                                     start: '2026-11-13T07:00:00Z', end: null, state: 'scheduled' } ] },
            timestamp: '2026-11-12T08:03:00Z'
        };

    return undefined;

}


describe('the state', () => {

    it('draws the line, the stations seen and the limits in force, and again on Reload', async () => {

        connected     = true;
        through       = null;
        notifications = 0;

        const root = await open(statePage, '/state', [ 'state:read' ],
                                controller, root => root.querySelector('#state-stations') !== null);

        assert.match(root.querySelector('#state-csms')!.textContent!, /connected/);
        assert.doesNotMatch(root.querySelector('#state-csms')!.textContent!, /not connected/);

        const rows = [ ...root.querySelectorAll('#state-stations tbody tr') ].map(row => row.getAttribute('data-station'));
        assert.deepEqual(rows, [ 'cs001', 'cs002' ]);
        assert.match(root.querySelector('tr[data-station="cs001"]')!.textContent!, /SSH/);
        assert.match(root.querySelector('tr[data-station="cs002"]')!.textContent!, /not connected/);

        const limits = root.querySelector('#state-limits')!.textContent!;
        assert.match(limits, /evening peak/);
        assert.doesNotMatch(limits, /tomorrow/, 'a limit not yet in force was shown as one');
        assert.match(limits, /Feed in at most\s+no limit/);

        connected = false;
        root.querySelector<HTMLButtonElement>('#reload')!.click();

        await until(() => /not connected/.test(root.querySelector('#state-csms')?.textContent ?? ''),
                    'Reload did not draw what the controller says then');

        assert.match(root.querySelector('tr[data-station="cs001"]')!.textContent!, /not connected/);

    });

    it('says how the VTN is reached through a connection, whether its events are subscribed to, the notifications and the last poll', async () => {

        connected     = true;
        through       = { transport: 'wss', binding: 'messages', subscribed: true };
        notifications = 3;

        const root = await open(statePage, '/state', [ 'state:read' ],
                                controller, root => root.querySelector('#state-limits') !== null);

        const text = (selector: string): string => root.querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

        assert.match(text('[data-openadr-binding]'),       /WebSocket over TLS, in the messages of OpenADR/);
        assert.equal(text('[data-openadr-subscribed]'),    'subscribed');
        assert.equal(text('[data-openadr-notifications]'), '3');
        assert.notEqual(text('[data-openadr-last-poll]'),  '', 'the last poll is not said');
        assert.notEqual(text('[data-openadr-last-poll]'),  'not yet');

        // The binding of the REST API subscribes to nothing, and says nothing of it.
        through       = { transport: 'ssh', binding: 'rest', subscribed: null };
        root.querySelector<HTMLButtonElement>('#reload')!.click();

        await until(() => /REST API/.test(text('[data-openadr-binding]')), 'Reload did not draw the binding the controller says then');

        assert.match(text('[data-openadr-binding]'),       /SSH, as the REST API of OpenADR in a tunnel/);
        assert.equal(root.querySelector('[data-openadr-subscribed]'), null, 'a subscription is said of the REST binding');

        // A URL of a connection that is not written down has no binding.
        through       = { transport: 'ws', binding: null, subscribed: null };
        root.querySelector<HTMLButtonElement>('#reload')!.click();

        await until(() => /no connection/.test(text('[data-openadr-binding]')), 'Reload did not say that there is no connection');

        assert.match(text('[data-openadr-binding]'),       /WebSocket, unencrypted/);

        // Over HTTP, there is no connection to say anything of.
        through       = null;
        root.querySelector<HTMLButtonElement>('#reload')!.click();

        await until(() => root.querySelector('[data-openadr-binding]') === null, 'Reload still says a connection for a VTN asked over HTTP');

        assert.equal(root.querySelector('[data-openadr-notifications]'), null);
        assert.notEqual(text('[data-openadr-last-poll]'),  '', 'the last poll is not said over HTTP');

    });

});
