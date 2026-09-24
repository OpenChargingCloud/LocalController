/**
 * What the NTS page tells the local controller when a time server is added,
 * edited or deleted, asked directly.
 *
 * Run with `npm test`. The controller is sent the whole list every time, so
 * what is pinned here is that the list sent is the list shown with exactly one
 * change in it - and that it reads the way the configuration file would.
 */

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { NTSTimeSource } from '../api/client';
import { entryOf, nameTaken, readable, withServer, withoutServer } from './ntsServers.ts';


const usual = { ntsKE: 4460, ntp: 123 };

/** A server as the local controller shows it. */
const shown = (hostname: string, more: Partial<NTSTimeSource> = {}): NTSTimeSource =>
    ({ hostname, priority: 0, ntsKEPort: 4460, ntpPort: 123, enabled: true, ...more });


describe('a time server turned back into its entry', () => {

    it('is a bare name when everything else is the usual', () => {

        assert.deepEqual(entryOf(shown('ptbtime1.ptb.de.'), usual),
                         { hostname: 'ptbtime1.ptb.de' });

    });

    it('says what is not the usual, and nothing that is', () => {

        assert.deepEqual(entryOf(shown('time.local.', { priority: 9, ntsKEPort: 4461, enabled: false }), usual),
                         { hostname: 'time.local', priority: 9, ntsKEPort: 4461, enabled: false });

    });

});


describe('the list a change sends', () => {

    const list = [ { hostname: 'a.example' }, { hostname: 'b.example' }, { hostname: 'c.example' } ];

    it('replaces exactly the one that was edited', () => {

        assert.deepEqual(withServer(list, 1, { hostname: 'b.example', enabled: false }),
                         [ { hostname: 'a.example' }, { hostname: 'b.example', enabled: false }, { hostname: 'c.example' } ]);

    });

    it('adds a new one at the end', () => {

        assert.deepEqual(withServer(list, null, { hostname: 'd.example' }).map(entry => entry.hostname),
                         [ 'a.example', 'b.example', 'c.example', 'd.example' ]);

    });

    it('leaves out exactly the one that was deleted', () => {

        assert.deepEqual(withoutServer(list, 0).map(entry => entry.hostname),
                         [ 'b.example', 'c.example' ]);

    });

    it('leaves the list it was made from alone, which is what the page goes back to when the controller says no', () => {

        withServer(list, 0, { hostname: 'x.example' });
        withoutServer(list, 2);

        assert.deepEqual(list.map(entry => entry.hostname), [ 'a.example', 'b.example', 'c.example' ]);

    });

});


describe('a name that is already taken', () => {

    const list = [ { hostname: 'ptbtime1.ptb.de' }, { hostname: 'ptbtime2.ptb.de' } ];

    it('is taken whatever its case and its root dot', () => {

        assert.equal(nameTaken(list, 'PTBTIME2.ptb.de.', null), true);

    });

    it('is not taken by the server being edited itself, or it could not be saved unchanged', () => {

        assert.equal(nameTaken(list, 'ptbtime2.ptb.de', 1), false);

    });

    it('is read without the root dot', () => {

        assert.equal(readable('ptbtime1.ptb.de.'), 'ptbtime1.ptb.de');
        assert.equal(readable('ptbtime1.ptb.de'),  'ptbtime1.ptb.de');

    });

});
