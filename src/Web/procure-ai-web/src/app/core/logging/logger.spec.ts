import { TestBed } from '@angular/core/testing';
import { AppConfigService, LoggerConfig } from '../config/app-config';
import { Logger } from './logger';

describe('Logger', () => {
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 202 }));
    vi.stubGlobal('fetch', fetchMock);
    for (const method of ['debug', 'info', 'warn', 'error'] as const) {
      vi.spyOn(console, method).mockImplementation(() => undefined);
    }
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  function create(logger: Partial<LoggerConfig> = {}) {
    const config = TestBed.inject(AppConfigService);
    config.config.update((c) => ({ ...c, logger: { ...c.logger, ...logger } }));
    return TestBed.inject(Logger);
  }

  const sentBatch = (call = 0) => JSON.parse(fetchMock.mock.calls[call][1].body as string);

  it('drops console entries below the configured level', () => {
    const logger = create({ consoleLevel: 'warn' });

    logger.debug('quiet');
    logger.info('quiet');
    logger.warn('loud');

    expect(console.debug).not.toHaveBeenCalled();
    expect(console.info).not.toHaveBeenCalled();
    expect(console.warn).toHaveBeenCalledOnce();
  });

  it('writes nothing at all when the level is off', () => {
    const logger = create({ consoleLevel: 'off', serverLevel: 'off' });

    logger.error('nothing');

    expect(console.error).not.toHaveBeenCalled();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('sends only entries at or above the server level', () => {
    const logger = create({ serverLevel: 'error', batchSize: 1 });

    logger.warn('not sent');
    logger.error('sent');

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(sentBatch().map((e: { message: string }) => e.message)).toEqual(['sent']);
  });

  it('batches 10 warn entries into one POST', () => {
    const logger = create({ batchSize: 10 });

    for (let i = 0; i < 9; i++) {
      logger.warn(`entry ${i}`);
    }
    expect(fetchMock).not.toHaveBeenCalled();
    logger.warn('entry 9');

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe('/api/client-logs');
    expect(init.method).toBe('POST');
    expect(sentBatch()).toHaveLength(10);
  });

  it('flushes a partial batch after flushMs', () => {
    vi.useFakeTimers();
    try {
      const logger = create({ batchSize: 10, flushMs: 5000 });

      logger.warn('lonely');
      expect(fetchMock).not.toHaveBeenCalled();
      vi.advanceTimersByTime(5000);

      expect(fetchMock).toHaveBeenCalledTimes(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it('uses sendBeacon when the page is hidden', () => {
    const sendBeacon = vi.fn().mockReturnValue(true);
    vi.stubGlobal('navigator', { sendBeacon });
    const logger = create({ batchSize: 10 });

    logger.error('about to leave');
    logger.flushOnPageHide();

    expect(sendBeacon).toHaveBeenCalledOnce();
    expect(sendBeacon.mock.calls[0][0]).toBe('/api/client-logs');
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('masks values of keys that look like secrets, also when nested', () => {
    const logger = create({ batchSize: 1 });

    logger.warn('login failed', {
      apiKey: 'x',
      user: { Password: 'p', name: 'Tom' },
      list: [{ access_token: 't' }],
      note: 'kept',
    });

    const expected = {
      apiKey: '***',
      user: { Password: '***', name: 'Tom' },
      list: [{ access_token: '***' }],
      note: 'kept',
    };
    expect(sentBatch()[0].data).toEqual(expected);
    expect(console.warn).toHaveBeenCalledWith(expect.stringContaining('login failed'), expected);
  });

  it('serialises an Error as message and stack', () => {
    const logger = create({ batchSize: 1 });

    logger.error('boom', new Error('bad thing'));

    const [entry] = sentBatch();
    expect(entry.data.message).toBe('bad thing');
    expect(entry.data.stack).toContain('bad thing');
  });

  it('rate-limits what it sends to the server', () => {
    const logger = create({ batchSize: 1, serverMaxPerMinute: 3 });

    for (let i = 0; i < 10; i++) {
      logger.error(`e${i}`);
    }

    expect(fetchMock).toHaveBeenCalledTimes(3);
  });

  it('never throws when the server is unreachable', () => {
    fetchMock.mockRejectedValue(new TypeError('offline'));
    const logger = create({ batchSize: 1 });

    expect(() => logger.error('still fine')).not.toThrow();
  });
});
