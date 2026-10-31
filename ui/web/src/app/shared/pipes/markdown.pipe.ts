import { Pipe, PipeTransform } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';

/**
 * Pipe per rendere markdown in HTML, ottimizzato per la chat.
 * Processa riga per riga (evita <br> dentro liste/code/heading) e ridimensiona
 * i titoli per non risultare "enormi".
 * Supporta: **bold**, *italic*, `code`, ```code blocks```, [links](url), # headings, - / 1. lists, --- hr
 */
@Pipe({ name: 'markdown' })
export class MarkdownPipe implements PipeTransform {
  constructor(private sanitizer: DomSanitizer) {}

  transform(value: string): SafeHtml {
    if (!value) return '';

    const lines = value.replace(/\r\n/g, '\n').split('\n');
    const out: string[] = [];
    let inCode = false;
    let codeLines: string[] = [];
    let inList = false;
    let inOl = false;

    const inline = (s: string): string =>
      s
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        // inline code
        .replace(/`([^`]+)`/g, '<code>$1</code>')
        // bold
        .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
        // italic (evita di toccare i ** già processati)
        .replace(/(^|[^*])\*([^*\n]+)\*/g, '$1<em>$2</em>')
        // link
        .replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (m, txt, url) =>
          /^https?:\/\//i.test(url)
            ? `<a href="${url}" target="_blank" rel="noopener">${txt}</a>`
            : txt
        );

    const closeList = () => {
      if (inList) {
        out.push(inOl ? '</ol>' : '</ul>');
        inList = false;
        inOl = false;
      }
    };

    for (const line of lines) {
      // Code fence toggle
      if (/^```/.test(line.trim())) {
        if (!inCode) {
          closeList();
          inCode = true;
          codeLines = [];
        } else {
          out.push('<pre><code>' + codeLines.map(inline).join('\n') + '</code></pre>');
          inCode = false;
        }
        continue;
      }

      if (inCode) {
        codeLines.push(line);
        continue;
      }

      const trimmed = line.trim();

      // Blank line closes a list
      if (trimmed === '') {
        closeList();
        continue;
      }

      // Headings -> <p class="md-h"> (ridimensionati in CSS)
      const h = line.match(/^(#{1,6})\s+(.*)$/);
      if (h) {
        closeList();
        out.push(`<p class="md-h">${inline(h[2])}</p>`);
        continue;
      }

      // Unordered list item
      const ul = line.match(/^\s*[-*]\s+(.*)$/);
      if (ul) {
        if (!inList || inOl) {
          closeList();
          out.push('<ul>');
          inList = true;
          inOl = false;
        }
        out.push(`<li>${inline(ul[1])}</li>`);
        continue;
      }

      // Ordered list item
      const ol = line.match(/^\s*\d+[.)]\s+(.*)$/);
      if (ol) {
        if (!inList || !inOl) {
          closeList();
          out.push('<ol>');
          inList = true;
          inOl = true;
        }
        out.push(`<li>${inline(ol[1])}</li>`);
        continue;
      }

      // Horizontal rule
      if (/^(-{3,}|\*{3,}|_{3,})$/.test(trimmed)) {
        closeList();
        out.push('<hr>');
        continue;
      }

      // Regular paragraph line
      closeList();
      out.push(`<p>${inline(line)}</p>`);
    }

    // Close any open block
    if (inCode) {
      out.push('<pre><code>' + codeLines.map(inline).join('\n') + '</code></pre>');
    }
    closeList();

    return this.sanitizer.bypassSecurityTrustHtml(out.join(''));
  }
}