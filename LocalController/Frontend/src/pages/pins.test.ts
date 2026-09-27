/**
 * What a time server or a name server is held to, as the pages edit it and
 * the local controller is told it, asked directly.
 *
 * Run with `npm test`. What is pinned is that a pin goes to the local
 * controller in the keys its file says it with and nowhere else - a pin that
 * also stays behind under a key it was written with before is two pins, and a
 * pin under a key the controller does not read is none - and that what
 * somebody pastes is read as the fingerprint they meant or refused, never
 * shortened into another one. The same as the vehicle's, from the same rules.
 */

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import type { ServerPins } from '../api/client';
import { draftOf, keysOf, noPins, normalisedFingerprint, parseFingerprints, pinsIn, pinsText, readPins, withPins } from './pins.ts';


const root         = 'a'.repeat(64);
const otherRoot    = 'd'.repeat(64);
const certificate  = 'b'.repeat(64);
const renewal      = 'c'.repeat(64);

/** What the controller says a server is held to. */
const heldTo = (more: Partial<ServerPins>): ServerPins =>
    ({ certificate: null, root: null, certificates: [], roots: [], onMismatch: 'refuse', trustOnFirstUse: null, ...more });


describe('a fingerprint as somebody writes it', () => {

    it('is read with colons, spaces or dashes, in either case', () => {

        const written = 'AA:'.repeat(31) + 'AA';

        assert.equal(normalisedFingerprint(written),                         root);
        assert.equal(normalisedFingerprint(written.replaceAll(':', ' ')),    root);
        assert.equal(normalisedFingerprint(written.replaceAll(':', '-')),    root);

    });

    it('is read out of what openssl prints', () => {

        assert.equal(normalisedFingerprint('sha256 Fingerprint=' + 'AA:'.repeat(31) + 'AA'), root);

    });

    it('is not one when it is shorter, longer, or not hexadecimal', () => {

        assert.equal(normalisedFingerprint('a'.repeat(63)),       null);
        assert.equal(normalisedFingerprint('a'.repeat(65)),       null);
        assert.equal(normalisedFingerprint('g' + 'a'.repeat(63)), null);
        assert.equal(normalisedFingerprint('a'.repeat(40)),       null, 'a SHA-1 thumbprint is not a SHA-256 fingerprint');

    });

    it('comes one to a line, each once, and what is not one is named', () => {

        assert.deepEqual(parseFingerprints(`${root}\n\n  ${root.toUpperCase()}  \r\n${certificate}\nnot one`),
                         { fingerprints: [ root, certificate ], wrong: [ 'not one' ] });

    });

});


describe('a draft told to the local controller', () => {

    it('says one pin of a kind the way the file said it before there could be several', () => {

        assert.deepEqual(keysOf({ ...noPins(), roots: [ root ] }),
                         { rootFingerprint: root });

    });

    it('says several as a list', () => {

        assert.deepEqual(keysOf({ ...noPins(), certificates: [ certificate, renewal ] }),
                         { certificateFingerprints: [ certificate, renewal ] });

    });

    it('says what a mismatch comes to only where it is not what a pin means anyway', () => {

        assert.deepEqual(keysOf({ ...noPins(), roots: [ root ], onMismatch: 'refuse' }), { rootFingerprint: root });
        assert.deepEqual(keysOf({ ...noPins(), roots: [ root ], onMismatch: 'record' }), { rootFingerprint: root, onMismatch: 'record' });

    });

    it('says nothing of a mismatch on a server held to nothing, which the controller would refuse', () => {

        assert.deepEqual(keysOf({ ...noPins(), onMismatch: 'accept' }), {});

    });

    it('says what it learns, and what a mismatch comes to after it has', () => {

        assert.deepEqual(keysOf({ ...noPins(), trustOnFirstUse: 'root', onMismatch: 'record' }),
                         { onMismatch: 'record', trustOnFirstUse: 'root' });

    });

    it('replaces every key the old pins were written with, so that none of them survives beside the new ones', () => {

        const entry = { hostname: 'time.local', certificateFingerprint: certificate, rootFingerprints: [ root, otherRoot ], onMismatch: 'record' as const };

        assert.deepEqual(withPins(entry, { ...noPins(), certificates: [ certificate, renewal ] }),
                         { hostname: 'time.local', certificateFingerprints: [ certificate, renewal ] });

        assert.deepEqual(withPins(entry, noPins()),
                         { hostname: 'time.local' });

    });

    it('leaves the entry it was made from alone, which is what the controller still has', () => {

        const entry = { hostname: 'time.local', rootFingerprint: root };

        withPins(entry, noPins());

        assert.deepEqual(entry, { hostname: 'time.local', rootFingerprint: root });

    });

});


describe('what a server is held to, read back', () => {

    it('from the controller, is what it says, and nothing where it says null', () => {

        assert.deepEqual(draftOf(heldTo({ root, roots: [ root, otherRoot ], onMismatch: 'accept', trustOnFirstUse: 'root' })),
                         { certificates: [], roots: [ root, otherRoot ], onMismatch: 'accept', trustOnFirstUse: 'root' });

        assert.deepEqual(draftOf(null), noPins());

    });

    it('from an entry, adds up one of a kind and several of a kind, as the file does', () => {

        assert.deepEqual(pinsIn({ rootFingerprint: root, rootFingerprints: [ otherRoot, root.toUpperCase() ] }),
                         { certificates: [], roots: [ root, otherRoot ], onMismatch: 'refuse', trustOnFirstUse: null });

    });

    it('goes back to the controller as it came', () => {

        const entry = { address: '1.1.1.1', rootFingerprint: root, certificateFingerprints: [ certificate, renewal ], trustOnFirstUse: 'root' as const };

        assert.deepEqual(withPins(entry, pinsIn(entry)), entry);

    });

});


describe('what a server is held to, in words', () => {

    it('names a fingerprint where something has a name for it, and by its first digits otherwise', () => {

        assert.equal(pinsText({ ...noPins(), roots: [ root, otherRoot ] }, fingerprint => fingerprint === root ? 'Our Root' : undefined),
                     `root Our Root or ${'d'.repeat(16)}…, refused otherwise`);

    });

    it('says what it will learn only while it has not learned it', () => {

        assert.equal(pinsText({ ...noPins(), trustOnFirstUse: 'root' }),
                     'its root once it is first believed, refused otherwise');

        assert.equal(pinsText({ ...noPins(), roots: [ root ], trustOnFirstUse: 'root', onMismatch: 'record' }),
                     `root ${'a'.repeat(16)}…, recorded otherwise`);

    });

    it('is nothing where the server is held to nothing', () => {

        assert.equal(pinsText(noPins()), null);

    });

});


describe('what the fields of a dialog say', () => {

    const fields = (more: Partial<{ certificates: string; roots: string; onMismatch: string; trustOnFirstUse: string }>) =>
        ({ certificates: '', roots: '', onMismatch: 'refuse', trustOnFirstUse: '', ...more });

    /** Lines, as a text area hands them over. */
    const lines = (...each: string[]) => each.join(String.fromCharCode(10));

    it('is the draft they describe', () => {

        assert.deepEqual(readPins(fields({ roots: lines(root, otherRoot), onMismatch: 'record', trustOnFirstUse: 'certificate' })),
                         { draft: { certificates: [], roots: [ root, otherRoot ], onMismatch: 'record', trustOnFirstUse: 'certificate' } });

    });

    it('is refused, naming the line, where a line is not a fingerprint', () => {

        assert.match(readPins(fields({ certificates: lines(certificate, '1a2b3c4d') })).error ?? '',
                     /^'1a2b3c4d' is not a SHA-256 fingerprint/);

    });

    it('is refused where it holds more than the controller takes', () => {

        const seventeen = lines(...Array.from({ length: 17 }, (_, index) => index.toString(16).padStart(64, '0')));

        assert.match(readPins(fields({ roots: seventeen })).error ?? '', /at most 16/);

    });

    it('learns nothing and refuses a mismatch where the lists say something else', () => {

        assert.deepEqual(readPins(fields({ onMismatch: 'whatever', trustOnFirstUse: 'none' })).draft, noPins());

    });

});
