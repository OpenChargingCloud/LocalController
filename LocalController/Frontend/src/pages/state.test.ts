/**
 * The state drawn, in a document of happy-dom, against a stand-in controller:
 * the line up to the CSMS, the stations seen and the limits in force, and
 * again on Reload with what the controller says then.
 */

import { open, until, type Asked } from '../../test/controller.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

const { statePage } = await import('./state.ts');


let connected = true;

function controller({ path }: Asked): unknown {

    if (path === '/state')
        return {
            csms:      { enabled: true, connected, connectedSince: connected ? '2026-11-12T08:00:00Z' : null },
            stations:  { connected: connected ? 1 : 0,
                         seen: [ { id: 'cs001', connected, over: connected ? 'SSH' : null,
                                   connectedSince: connected ? '2026-11-12T08:01:00Z' : null, lastSeen: '2026-11-12T08:02:00Z' },
                                 { id: 'cs002', connected: false, over: null, connectedSince: null, lastSeen: '2026-11-12T07:00:00Z' } ] },
            openADR:   { running: true, signedIn: true, lastPoll: '2026-11-12T08:00:00Z', lastRead: '2026-11-12T08:00:00Z',
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

        connected = true;

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

});
