import { Pipe, PipeTransform } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';

/**
 * Pipe che colorizza un diff unified: righe aggiunte (verdi), rimosse (rosse),
 * intestazioni di hunk (viola) e contesto (grigio). Il contenuto viene escappato
 * HTML e ogni riga viene avvolta in uno span colorato.
 */
@Pipe({ name: 'diff' })
export class DiffPipe implements PipeTransform {
  constructor(private sanitizer: DomSanitizer) {}

  transform(value?: string | null): SafeHtml {
    if (!value) return '';
    const lines = value.replace(/\r\n/g, '\n').split('\n');
    const esc = (s: string): string =>
      s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

    const out: string[] = [];
    for (const line of lines) {
      let cls = 'diff-line-ctx';
      if (line.startsWith('+++') || line.startsWith('---')) cls = 'diff-line-hunk';
      else if (line.startsWith('@@')) cls = 'diff-line-hunk';
      else if (line.startsWith('+')) cls = 'diff-line-add';
      else if (line.startsWith('-')) cls = 'diff-line-del';
      // preserva lo spazio iniziale di contesto: usa &nbsp; per la prima colonna
      const body = line.startsWith(' ') ? line.slice(1) : line;
      const leadingSpace = line.startsWith(' ') ? '&nbsp;' : '';
      out.push(`<span class="${cls}">${leadingSpace}${esc(body)}</span>`);
    }
    return this.sanitizer.bypassSecurityTrustHtml(out.join('\n'));
  }
}
