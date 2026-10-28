import { computed, inject, Signal } from '@angular/core';
import { ApprovalPreviewDto } from '../../api/approvals/models/approval-preview-dto';
import { PreviewStepDto } from '../../api/approvals/models/preview-step-dto';
import { ApprovalsService } from '../../api/approvals/services/approvals.service';
import { debouncedLookup } from './debounced-lookup';

export type ApproversPreviewState =
  | { kind: 'idle' }
  | { kind: 'checking' }
  | { kind: 'steps'; steps: PreviewStepDto[] }
  /** No approver exists for this amount and cost center: the request could not be approved, so Submit is blocked. */
  | { kind: 'nobody'; reason: string }
  | { kind: 'unknown' };

type Preview = (params: { costCenter: string; amount: number }) => Promise<ApprovalPreviewDto>;

/** The "Approvals needed" tile: the approval steps and approver names for the form's total, from Approvals. */
export function createApproversPreview(
  costCenter: () => string,
  amount: () => number,
  preview?: Preview,
): { state: Signal<ApproversPreviewState> } {
  const api = preview ? null : inject(ApprovalsService); // resolved now: inject() is not allowed later, when the timer fires
  const raw = debouncedLookup(() => (costCenter() !== '' && amount() > 0 ? { costCenter: costCenter(), amount: amount() } : null), preview ?? ((p) => api!.previewApproval(p)));
  const state = computed<ApproversPreviewState>(() => {
    const s = raw();
    switch (s.status) {
      case 'idle':
        return { kind: 'idle' };
      case 'loading':
        return { kind: 'checking' };
      case 'error':
        return { kind: 'unknown' };
      case 'loaded':
        return s.value.possible
          ? { kind: 'steps', steps: s.value.steps }
          : { kind: 'nobody', reason: s.value.reason ?? 'No approver is available for this request.' };
    }
  });
  return { state };
}
