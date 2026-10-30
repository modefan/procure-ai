import { Component, computed, ElementRef, input, output, signal, viewChild } from '@angular/core';

/** "10 MB" for the limit shown to the user. */
const sizeLabel = (bytes: number): string => (bytes >= 1024 * 1024 ? `${Math.round((bytes / (1024 * 1024)) * 10) / 10} MB` : `${Math.ceil(bytes / 1024)} KB`);

/** "PDF or Markdown" from an accept list; the second Markdown extension is not repeated. */
const typeNames = (extensions: string[]): string[] =>
  extensions.map((e) => (e === '.md' ? 'Markdown' : e.replace('.', '').toUpperCase())).filter((t) => t !== 'MARKDOWN');

/** Why a file may not be uploaded, or null when it may. Used by the drop zone and by Replace buttons. */
export function rejectionReason(file: File, accept: string, maxBytes: number): string | null {
  const extensions = accept.split(',').map((e) => e.trim().toLowerCase()).filter((e) => e !== '');
  if (extensions.length > 0 && !extensions.some((e) => file.name.toLowerCase().endsWith(e))) {
    return `"${file.name}" is not a ${typeNames(extensions).join(' or ')} file.`;
  }
  if (file.size > maxBytes) {
    return `"${file.name}" is larger than ${sizeLabel(maxBytes)}.`;
  }
  return file.size === 0 ? `"${file.name}" is empty.` : null;
}

/**
 * Pick or drop one file. Checks the type (`accept`: a comma list of extensions, e.g. ".pdf,.md") and the size (`maxBytes`)
 * in the browser before anything is sent, and shows why a file was refused; a good file is emitted as `fileSelected`.
 * Keyboard: Enter or Space on the zone opens the file picker. While `busy` the zone is off. The server checks the file again.
 */
@Component({
  selector: 'app-upload-drop-zone',
  template: `
    <div
      data-testid="drop-zone"
      role="button"
      [tabindex]="busy() ? -1 : 0"
      [attr.aria-busy]="busy() ? 'true' : null"
      [attr.aria-disabled]="busy() ? 'true' : null"
      [attr.aria-describedby]="hintId"
      class="drop-zone"
      [class.drop-zone-over]="over()"
      [class.drop-zone-busy]="busy()"
      (click)="open()"
      (keydown.enter)="onKey($event)"
      (keydown.space)="onKey($event)"
      (dragover)="onDragOver($event)"
      (dragleave)="over.set(false)"
      (drop)="onDrop($event)"
    >
      @if (busy()) {
        <span class="size-5 animate-spin rounded-full border-2 border-indigo-700 border-t-transparent" aria-hidden="true"></span>
        <span class="font-semibold text-slate-800">{{ busyLabel() }}</span>
      } @else {
        <span class="font-semibold text-slate-800"><ng-content>Drop a file here, or click to choose one</ng-content></span>
      }
      <span [id]="hintId" class="text-sm text-slate-600">{{ hint() }}</span>
    </div>
    <input #picker type="file" class="sr-only" tabindex="-1" aria-hidden="true" [accept]="accept()" (change)="onPicked()" />
    @if (rejected(); as reason) {
      <p role="alert" class="field-error mt-2">{{ reason }}</p>
    }
  `,
})
export class UploadDropZone {
  /** The accepted extensions, like the `accept` of a file input: ".pdf,.md,.markdown". */
  readonly accept = input('');
  readonly maxBytes = input(10 * 1024 * 1024);
  readonly busy = input(false);
  readonly busyLabel = input('Uploading…');
  readonly fileSelected = output<File>();

  protected readonly hintId = `drop-hint-${Math.random().toString(36).slice(2, 8)}`;
  protected readonly over = signal(false);
  /** Why the last file was refused; null once a good file came. */
  protected readonly rejected = signal<string | null>(null);
  private readonly picker = viewChild.required<ElementRef<HTMLInputElement>>('picker');

  private readonly extensions = computed(() =>
    this.accept()
      .split(',')
      .map((e) => e.trim().toLowerCase())
      .filter((e) => e !== ''),
  );
  protected readonly hint = computed(() => {
    const types = typeNames(this.extensions());
    return `${types.length ? types.join(' or ') + ' · ' : ''}up to ${sizeLabel(this.maxBytes())}`;
  });

  protected open(): void {
    if (!this.busy()) {
      this.picker().nativeElement.click();
    }
  }

  protected onKey(event: Event): void {
    event.preventDefault();
    this.open();
  }

  protected onDragOver(event: DragEvent): void {
    event.preventDefault();
    if (!this.busy()) {
      this.over.set(true);
    }
  }

  protected onDrop(event: DragEvent): void {
    event.preventDefault();
    this.over.set(false);
    const file = event.dataTransfer?.files?.[0];
    if (file && !this.busy()) {
      this.take(file);
    }
  }

  protected onPicked(): void {
    const input = this.picker().nativeElement;
    const file = input.files?.[0];
    if (file) {
      this.take(file);
    }
    // The same file can then be chosen again.
    try {
      input.value = '';
    } catch {
      // Not allowed on some test doubles.
    }
  }

  private take(file: File): void {
    const reason = rejectionReason(file, this.accept(), this.maxBytes());
    this.rejected.set(reason);
    if (!reason) {
      this.fileSelected.emit(file);
    }
  }
}
