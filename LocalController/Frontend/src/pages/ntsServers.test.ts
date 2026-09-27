/**
 * What the NTS page tells the local controller when a time server is added, edited or
 * deleted, asked directly.
 *
 * Run with `npm test`. The local controller is sent the whole list every time, so what
 * is pinned here is that the list sent is the list shown with exactly one
 * change in it, what each server is held to included - and that it reads the
 * way the configuration file would.
 */

import { strict as assert }  from 'node:assert';
import { readFileSync }      from 'node:fs';
import { registerHooks }     from 'node:module';
import { describe, it }      from 'node:test';

import type { NTSTimeSource, ServerPins } from '../api/client';

// The page's helpers are written for webpack, which does not want the
// extension in a relative import, and they import what a server is held to
// from pins.ts; Node wants the extension. One hook puts it back - see the
// client's test.
registerHooks({
    resolve(specifier, context, next) {
        return specifier.startsWith('.') && !specifier.endsWith('.ts')
                   ? next(`${specifier}.ts`, context)
                   : next(specifier, context);
    }
});

const { entryOf, nameTaken, readable, sentOf, withServer, withoutServer } = await import('./ntsServers.ts');


const usual = { ntsKE: 4460, ntp: 123 };

/** A server as the local controller shows it. */
const shown = (hostname: string, more: Partial<NTSTimeSource> = {}): NTSTimeSource =>
    ({ hostname, priority: 0, ntsKEPort: 4460, ntpPort: 123, enabled: true, ...more });

/** What a server is held to as the local controller shows it: nothing beyond what is given. */
const heldTo = (more: Partial<ServerPins>): ServerPins =>
    ({ certificate: null, root: null, certificates: [], roots: [], onMismatch: 'refuse', trustOnFirstUse: null, ...more });

/** The root ptbtime1.ptb.de and ptbtime2.ptb.de were believed with, and the certificates they showed. */
const root          = '96bcec06264976f37460779acf28c5a7cfe8a3c0aae11a8ffcee05c0bddf08c6';
const certificate1  = '244656b8f4d6e058c1a34f636d801fc7a92eff126dc98d7adb50cffaeed4d791';
const certificate2  = 'e2a0ad9e30fddb7d5b35b692159723a1aa96dd3062142456b3795b8638636472';


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


describe('what a time server is held to', () => {

    it('goes back into its entry, or the next save of any server holds it to nothing', () => {

        // What a local controller said of ptbtime1.ptb.de once it had learned
        // its root on first use, as it said it.
        const learned: ServerPins = { certificate: null, root, certificates: [], roots: [ root ], onMismatch: 'refuse', trustOnFirstUse: 'root' };

        assert.deepEqual(entryOf(shown('ptbtime1.ptb.de.', { heldTo: learned }), usual),
                         { hostname: 'ptbtime1.ptb.de', rootFingerprint: root, trustOnFirstUse: 'root' });

    });

    it('is written the way the file writes it: one under the name a pin always had, several as a list', () => {

        assert.deepEqual(entryOf(shown('time.example.', { heldTo: heldTo({ certificate: certificate1, root, certificates: [ certificate1, certificate2 ], roots: [ root ] }) }), usual),
                         { hostname: 'time.example', certificateFingerprints: [ certificate1, certificate2 ], rootFingerprint: root });

    });

    it('says what a mismatch comes to only where it is not a refusal, which is what a pin means anyway', () => {

        assert.deepEqual(entryOf(shown('time.example.', { heldTo: heldTo({ certificate: certificate1, certificates: [ certificate1 ], onMismatch: 'record' }) }), usual),
                         { hostname: 'time.example', certificateFingerprint: certificate1, onMismatch: 'record' });

    });

    it('is nothing at all for a server held to nothing', () => {

        assert.deepEqual(entryOf(shown('ptbtime2.ptb.de.', { heldTo: null }), usual),
                         { hostname: 'ptbtime2.ptb.de' });

    });

    it('stays with a server when another one is saved', () => {

        // What was measured: the dialog of ptbtime2.ptb.de, its priority
        // changed to 1, took the root ptbtime1.ptb.de had learned out of the
        // file and out of effect - and the instruction to learn it.
        const list = [ entryOf(shown('ptbtime1.ptb.de.', { heldTo: heldTo({ root, roots: [ root ], trustOnFirstUse: 'root' }) }), usual),
                       entryOf(shown('ptbtime2.ptb.de.'), usual) ];

        assert.deepEqual(withServer(list, 1, { hostname: 'ptbtime2.ptb.de', priority: 1 }),
                         [ { hostname: 'ptbtime1.ptb.de', rootFingerprint: root, trustOnFirstUse: 'root' },
                           { hostname: 'ptbtime2.ptb.de', priority: 1 } ]);

    });

});


describe('a time server sent back', () => {

    it('goes with what the page showed it held to, so that a root it learned while the page was open is kept', () => {

        // What was measured: ptbtime1.ptb.de learned its root on first use
        // after the page was loaded, and the save of another server's
        // priority took it out of the file and out of effect. The entry says
        // what it is held to as the page shows it; beside it, what the page
        // showed, from which the local controller can tell that the root was
        // not taken away here.
        assert.deepEqual(sentOf(shown('ptbtime1.ptb.de.', { heldTo: heldTo({ trustOnFirstUse: 'root' }) }), usual),
                         { hostname: 'ptbtime1.ptb.de', trustOnFirstUse: 'root', pinsAsShown: { trustOnFirstUse: 'root' } });

    });

    it('says a server shown held to nothing was shown so', () => {

        assert.deepEqual(sentOf(shown('ptbtime2.ptb.de.', { heldTo: null }), usual),
                         { hostname: 'ptbtime2.ptb.de', pinsAsShown: {} });

    });

    it('is what the NTS page sends every server of its list as', () => {

        const page = readFileSync(new URL('./nts.ts', import.meta.url), 'utf-8');

        assert.match(page, /\.map\(source => sentOf\(source, usual\)\)/,
                     'the NTS page sends its list without what it showed each server held to');

    });

});


describe('the entry the dialog saves', () => {

    // Asked of the page's source, as Node has no browser to open it in, and
    // the views it draws with cannot be loaded here: the dialog's own entry,
    // sent as it was typed, is what took the pins.
    const page = readFileSync(new URL('./nts.ts', import.meta.url), 'utf-8');

    it('shows what the server is held to, in fields of its own', () => {

        assert.match(page, /pinsFieldset\(draftOf\(shown\?\.heldTo\), \{/,
                     'the NTS dialog does not show what the server it edits is held to');

    });

    it('is saved with what those fields say - under the same name or another, where it is to be seen and emptied', () => {

        assert.match(page, /const pins = readPinsFieldset\(form\);/,
                     'the NTS dialog does not read what its pin fields say');

        assert.match(page, /withServer\(list, index, withPins\(entry, pins\.draft\)\)/,
                     'the NTS dialog saves a server without what its pin fields say');

    });

    it('goes with what the dialog showed the server held to, where it edits one the page loaded', () => {

        assert.match(page, /if \(shown !== null\)\s+entry\.pinsAsShown = asShown\(shown\.heldTo\);/,
                     'the NTS dialog saves a server without what it showed it held to');

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

    it('leaves the list it was made from alone, which is what the page goes back to when the local controller says no', () => {

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
