/**
 * Which entry a drawn line shows.
 *
 * The log page draws newest first while the store keeps its entries oldest
 * first, and it pairs the two by position: a line is built once and then only
 * told whether a filter still wants it, which is what keeps a keystroke in the
 * search box at two milliseconds instead of six hundred. That pairing is the
 * one thing holding the arrangement together, and it is invisible - get it
 * wrong by one and every line shows one entry while being filtered by another,
 * with nothing on screen to say so.
 *
 * So it lives here, as two functions that must agree, rather than as an index
 * expression written out at each of the three places that needs it.
 */

/** The entries in the order they are drawn: newest first. */
export function drawOrder<T>(entries: readonly T[]): T[] {
    return [...entries].reverse();
}

/**
 * The entry the line at this position was built from, for a list drawn by
 * drawOrder and kept the same length as the store.
 */
export function entryAt<T>(entries: readonly T[], line: number): T | undefined {
    return entries[entries.length - 1 - line];
}
