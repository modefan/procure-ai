import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, ElementRef, inject, OnInit, signal, viewChild } from '@angular/core';
import { PolicyDocumentDto } from '../../api/assistant/models/policy-document-dto';
import { rejectionReason, UploadDropZone } from '../../core/ui/upload-drop-zone';
import { POLICY_MAX_BYTES, PoliciesStore } from './policies.store';

const BADGE = { ok: 'badge-live', warn: 'badge-warn', bad: 'badge-error', idle: 'badge-muted' } as const;
const ACCEPT = '.pdf,.md,.markdown';

/** How a status looks: the badge label, its colour and (for Flagged and Failed) the reason beneath. */
interface StatusView {
  label: string;
  badge: string;
  progress: number | null;
  reason: string | null;
}

const statusOf = (d: PolicyDocumentDto): StatusView => {
  switch (d.status) {
    case 'Ready':
      return { label: 'Ready', badge: BADGE.ok, progress: null, reason: null };
    case 'Embedding':
      return { label: `Embedding… ${d.progress}%`, badge: BADGE.warn, progress: d.progress, reason: null };
    case 'Flagged':
      return { label: 'Flagged', badge: BADGE.bad, progress: null, reason: d.flaggedReason ?? 'The document guard refused this file.' };
    case 'Failed':
      return { label: 'Failed', badge: BADGE.bad, progress: null, reason: d.failureReason ?? 'The file could not be processed.' };
    case 'Checking':
      return { label: 'Checking…', badge: BADGE.idle, progress: null, reason: null };
    default:
      return { label: 'Queued…', badge: BADGE.idle, progress: null, reason: null };
  }
};

/**
 * Screen 17: the policy documents the assistant answers from (needs `admin.policies`). Upload by drop or click (PDF or Markdown,
 * 10 MB, checked here and again by the server); each row shows its version, chunks and status with live progress while it is
 * embedded; Replace picks a new file, Remove asks first. A table from the lg breakpoint up; below it, one card per document.
 */
@Component({
  selector: 'app-policies-page',
  imports: [UploadDropZone, NgTemplateOutlet],
  template: `
    <section class="mx-auto max-w-6xl px-4 py-8">
      <h1 class="text-2xl font-bold tracking-tight text-slate-900">Policy documents</h1>
      <p class="mt-1 text-slate-600">What the AI assistant knows. Uploads are read, checked and embedded in the background.</p>

      @if (store.error(); as message) {
        <div role="alert" class="mt-6 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">
          <span>{{ message }}</span>
          <button type="button" class="btn-secondary" (click)="retry()">Try again</button>
        </div>
      }
      @if (problem(); as message) {
        <div role="alert" data-testid="action-error" class="mt-6 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">
          <span>{{ message }}</span>
          <button type="button" class="btn-secondary px-3 py-1" (click)="dismiss()">Dismiss</button>
        </div>
      }

      @if (store.isLoading() && store.entities().length === 0) {
        <div class="card mt-6 space-y-4" role="status" aria-live="polite">
          <span class="sr-only">Loading policy documents</span>
          @for (row of skeletonRows; track row) {
            <div class="h-6 animate-pulse rounded bg-slate-200" aria-hidden="true"></div>
          }
        </div>
      } @else if (store.entities().length === 0 && !store.error()) {
        <div class="card mt-6 py-12 text-center">
          <p class="text-lg font-semibold text-slate-900">No policy documents yet</p>
          <p class="mt-1 text-slate-600">Upload one below and the assistant can answer from it once it is ready.</p>
        </div>
      } @else if (store.entities().length > 0) {
        <div class="mt-6" [class.opacity-60]="store.isLoading()">
          <div class="hidden overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm lg:block">
            <table class="w-full table-fixed text-left text-sm">
              <caption class="sr-only">Policy documents the assistant answers from</caption>
              <colgroup>
                <col />
                <col class="w-28" />
                <col class="w-24" />
                <col class="w-72" />
                <col class="w-52" />
              </colgroup>
              <thead class="border-b border-slate-200 bg-slate-50 text-xs font-semibold uppercase tracking-wide text-slate-600">
                <tr>
                  <th scope="col" class="px-4 py-3 whitespace-nowrap">Document</th>
                  <th scope="col" class="px-4 py-3 whitespace-nowrap">Version</th>
                  <th scope="col" class="px-4 py-3 text-right whitespace-nowrap">Chunks</th>
                  <th scope="col" class="px-4 py-3 whitespace-nowrap">Status</th>
                  <th scope="col" class="px-4 py-3 text-right whitespace-nowrap"><span class="sr-only">Actions</span></th>
                </tr>
              </thead>
              <tbody class="divide-y divide-slate-100">
                @for (doc of store.entities(); track doc.id) {
                  @let status = viewOf(doc);
                  <tr class="align-top hover:bg-slate-50" [attr.data-document]="doc.id">
                    <th scope="row" class="px-4 py-3 font-normal">
                      <p class="truncate font-semibold text-slate-900" [title]="doc.title">{{ doc.title }}</p>
                      <p class="truncate text-xs text-slate-600" [title]="doc.fileName">{{ doc.fileName }} &middot; {{ doc.uploadedByName }}</p>
                    </th>
                    <td class="px-4 py-3 whitespace-nowrap tabular-nums text-slate-900">
                      {{ doc.version }}
                      @if (doc.currentVersion !== doc.version && doc.currentVersion > 0) {
                        <span class="block text-xs text-slate-600">v{{ doc.currentVersion }} in use</span>
                      }
                    </td>
                    <td class="px-4 py-3 text-right whitespace-nowrap tabular-nums text-slate-900" data-cell="chunks">{{ doc.chunkCount > 0 ? doc.chunkCount : '—' }}</td>
                    <td class="px-4 py-3" data-cell="status">
                      <ng-container *ngTemplateOutlet="statusCell; context: { $implicit: status, doc: doc }" />
                    </td>
                    <td class="px-4 py-3 text-right whitespace-nowrap">
                      <ng-container *ngTemplateOutlet="actions; context: { $implicit: doc }" />
                    </td>
                  </tr>
                }
                <tr>
                  <td colspan="5" class="px-4 py-3 text-slate-600" data-testid="auto-note">Past purchases are added automatically when a request is completed.</td>
                </tr>
              </tbody>
            </table>
          </div>

          <ul class="space-y-3 lg:hidden" aria-label="Policy documents">
            @for (doc of store.entities(); track doc.id) {
              @let status = viewOf(doc);
              <li class="card p-4" [attr.data-document-card]="doc.id">
                <p class="truncate font-semibold text-slate-900" [title]="doc.title">{{ doc.title }}</p>
                <p class="truncate text-xs text-slate-600" [title]="doc.fileName">{{ doc.fileName }} &middot; {{ doc.uploadedByName }}</p>
                <dl class="mt-3 grid grid-cols-[auto_minmax(0,1fr)] items-center gap-x-4 gap-y-1 text-sm">
                  <dt class="text-slate-600">Version</dt>
                  <dd class="text-right tabular-nums text-slate-900">{{ doc.version }}</dd>
                  <dt class="text-slate-600">Chunks</dt>
                  <dd class="text-right tabular-nums text-slate-900">{{ doc.chunkCount > 0 ? doc.chunkCount : '—' }}</dd>
                </dl>
                <div class="mt-3">
                  <ng-container *ngTemplateOutlet="statusCell; context: { $implicit: status, doc: doc }" />
                </div>
                <div class="mt-3 flex justify-end">
                  <ng-container *ngTemplateOutlet="actions; context: { $implicit: doc }" />
                </div>
              </li>
            }
            <li class="px-1 text-sm text-slate-600">Past purchases are added automatically when a request is completed.</li>
          </ul>
        </div>
      }

      <div class="mt-6" data-testid="upload">
        <app-upload-drop-zone [accept]="accept" [maxBytes]="maxBytes" [busy]="store.uploading()" busyLabel="Uploading…" (fileSelected)="upload($event)">
          Upload a policy document
        </app-upload-drop-zone>
      </div>

      <input #replacePicker data-testid="replace-input" type="file" class="sr-only" tabindex="-1" aria-hidden="true" [accept]="accept" (change)="onReplacePicked()" />
    </section>

    <ng-template #statusCell let-status let-doc="doc">
      <span [class]="status.badge" [attr.data-status]="doc.status">{{ status.label }}</span>
      @if (status.progress !== null) {
        <div
          class="progress-track mt-2"
          data-progress
          role="progressbar"
          [attr.aria-label]="'Embedding ' + doc.title"
          aria-valuemin="0"
          aria-valuemax="100"
          [attr.aria-valuenow]="status.progress"
          [style.--progress]="status.progress + '%'"
        >
          <span class="progress-fill"></span>
        </div>
      }
      @if (status.reason) {
        <p class="mt-1 text-xs text-red-800 break-words" data-cell="reason">{{ status.reason }}</p>
      }
    </ng-template>

    <ng-template #actions let-doc>
      <div class="inline-flex items-center gap-2">
        <button
          type="button"
          class="btn-secondary px-3 py-1 disabled:cursor-not-allowed disabled:opacity-60"
          data-action="replace"
          [disabled]="store.busyId() !== null || store.uploading()"
          [attr.aria-label]="'Replace ' + doc.title"
          (click)="replace(doc.id)"
        >
          {{ store.busyId() === doc.id ? 'Working…' : 'Replace' }}
        </button>
        <button
          type="button"
          class="btn-secondary px-3 py-1 text-red-800 disabled:cursor-not-allowed disabled:opacity-60"
          data-action="remove"
          [disabled]="store.busyId() !== null || store.uploading()"
          [attr.aria-label]="'Remove ' + doc.title"
          (click)="remove(doc.id)"
        >
          Remove
        </button>
      </div>
    </ng-template>
  `,
})
export class PoliciesPage implements OnInit {
  protected readonly store = inject(PoliciesStore);
  protected readonly accept = ACCEPT;
  protected readonly maxBytes = POLICY_MAX_BYTES;
  protected readonly skeletonRows = [1, 2, 3];
  private readonly replacePicker = viewChild.required<ElementRef<HTMLInputElement>>('replacePicker');
  /** The document whose file the open picker will replace. */
  private replacing: string | null = null;
  /** A file refused in the browser (Replace); the store's refusals come from the server. */
  private readonly localProblem = signal<string | null>(null);
  protected readonly problem = computed(() => this.localProblem() ?? this.store.actionError());

  protected viewOf(doc: PolicyDocumentDto): StatusView {
    return statusOf(doc);
  }

  protected upload(file: File): void {
    this.localProblem.set(null);
    void this.store.upload(file);
  }

  protected replace(id: string): void {
    this.replacing = id;
    this.localProblem.set(null);
    this.replacePicker().nativeElement.click();
  }

  protected onReplacePicked(): void {
    const input = this.replacePicker().nativeElement;
    const file = input.files?.[0];
    const id = this.replacing;
    this.replacing = null;
    try {
      input.value = '';
    } catch {
      // Not allowed on some test doubles.
    }
    if (!file || !id) {
      return;
    }
    const reason = rejectionReason(file, ACCEPT, POLICY_MAX_BYTES);
    if (reason) {
      this.localProblem.set(reason);
      return;
    }
    void this.store.replace(id, file);
  }

  protected remove(id: string): void {
    this.localProblem.set(null);
    void this.store.remove(id);
  }

  protected dismiss(): void {
    this.localProblem.set(null);
    this.store.clearActionError();
  }

  protected retry(): void {
    void this.store.load();
  }

  ngOnInit(): void {
    void this.store.load();
  }
}
