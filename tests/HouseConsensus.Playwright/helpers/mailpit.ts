import { expect, type APIRequestContext } from '@playwright/test';

interface Address { Address?: string; Name?: string }
interface MessageSummary { ID: string; To?: Address[]; Subject?: string; Created?: string }
interface MessageList { messages?: MessageSummary[]; Messages?: MessageSummary[] }
interface MessageDetail { HTML?: string; Text?: string; Subject?: string }

const linkPattern = /https?:\/\/[^\s"'<>]+/g;

export class MailpitClient {
  constructor(
    private readonly request: APIRequestContext,
    private readonly baseURL = process.env.MAILPIT_API_URL ?? 'http://127.0.0.1:8025'
  ) {}

  async clear(): Promise<void> {
    const response = await this.request.delete(`${this.baseURL}/api/v1/messages`);
    expect(response.ok(), `Mailpit clear failed: ${response.status()}`).toBeTruthy();
  }

  async waitForLink(recipient: string, options: { subject?: RegExp; timeout?: number } = {}): Promise<string> {
    const timeout = options.timeout ?? 20_000;
    let link: string | undefined;

    await expect.poll(async () => {
      const response = await this.request.get(`${this.baseURL}/api/v1/messages`);
      if (!response.ok()) return `mailpit-http-${response.status()}`;
      const body = await response.json() as MessageList;
      const messages = body.messages ?? body.Messages ?? [];
      const candidate = messages.find(message =>
        message.To?.some(to => to.Address?.toLowerCase() === recipient.toLowerCase()) &&
        (!options.subject || options.subject.test(message.Subject ?? ''))
      );
      if (!candidate) return 'message-not-found';

      const detailResponse = await this.request.get(`${this.baseURL}/api/v1/message/${candidate.ID}`);
      if (!detailResponse.ok()) return `message-http-${detailResponse.status()}`;
      const detail = await detailResponse.json() as MessageDetail;
      const links = `${detail.HTML ?? ''}\n${detail.Text ?? ''}`.match(linkPattern) ?? [];
      link = links.map(value => value.replace(/&amp;/g, '&')).find(value =>
        /magic|token|invite|sign-?in|login/i.test(value)
      ) ?? links[0];
      return link ? 'found' : 'link-not-found';
    }, { timeout, message: `magic/invite email for ${recipient}` }).toBe('found');

    if (!link) throw new Error(`Mail arrived for ${recipient}, but contained no link`);
    return link;
  }
}
