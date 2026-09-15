// A tiny tagged template for building HTML strings safely: every interpolated
// value is escaped unless it is itself an html`...` fragment (or raw(...)).

export class HTMLFragment {
    constructor(public readonly value: string) {}
    toString(): string {
        return this.value;
    }
}

export function raw(value: string): HTMLFragment {
    return new HTMLFragment(value);
}

export function escapeHTML(text: string): string {
    return text.replace(/[&<>"']/g, character => ({
        '&': '&amp;',
        '<': '&lt;',
        '>': '&gt;',
        '"': '&quot;',
        "'": '&#39;'
    }[character] ?? character));
}

function toHTML(value: unknown): string {

    if (value === null || value === undefined || value === false)
        return '';

    if (value instanceof HTMLFragment)
        return value.value;

    if (Array.isArray(value))
        return value.map(toHTML).join('');

    return escapeHTML(String(value));

}

export function html(strings: TemplateStringsArray, ...values: unknown[]): HTMLFragment {

    let out = '';

    strings.forEach((part, index) => {
        out += part;
        if (index < values.length)
            out += toHTML(values[index]);
    });

    return new HTMLFragment(out);

}

/** Replace the content of an element with a fragment. */
export function render(target: HTMLElement, fragment: HTMLFragment): void {
    target.innerHTML = fragment.value;
}

/** querySelector that throws instead of returning null. */
export function must<T extends Element>(root: ParentNode, selector: string): T {

    const element = root.querySelector<T>(selector);

    if (element === null)
        throw new Error(`Element '${selector}' not found!`);

    return element;

}
