using System.Text.Json;
using FTMS.Application;
using FTMS.Domain;
using Microsoft.Web.WebView2.Wpf;

namespace FTMS.Desktop;

public sealed class WebViewFtmsClient(WebView2 webView, string ftmsUrl) : IFtmsClient
{
    private DateTimeOffset _lastLoginRecoveryAt = DateTimeOffset.MinValue;

    public async Task<bool> IsAuthenticatedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var url = await webView.Dispatcher.InvokeAsync(() => webView.Source?.AbsoluteUri ?? string.Empty);
        return url.Contains("/ihub/", StringComparison.OrdinalIgnoreCase) &&
            !url.Contains("/id/login", StringComparison.OrdinalIgnoreCase) &&
            !url.Contains("/adfs/", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<TicketSnapshot>> GetTicketsAsync(CancellationToken cancellationToken)
    {
        const string script = """
            (() => {
              const body = new URLSearchParams({ take: '1000', skip: '0', page: '1', pageSize: '1000',
                search: '', isMyTicket: '', isAkabot: '', strStatus: '', strRegionID: '', linkDeptId: '',
                alarmType: '0', isSortByDate: '' });
              const xhr = new XMLHttpRequest();
              xhr.__ftmsCompanionInternal = true;
              xhr.open('POST', '/ihub/request/GetListRequestV12', false);
              xhr.setRequestHeader('Content-Type', 'application/x-www-form-urlencoded; charset=UTF-8');
              xhr.setRequestHeader('X-Requested-With', 'XMLHttpRequest');
              try { xhr.send(body.toString()); }
              catch (error) { return JSON.stringify({ error: String(error) }); }
              if (xhr.status < 200 || xhr.status >= 300)
                return JSON.stringify({ error: `FTMS API HTTP ${xhr.status}` });

              let response;
              try { response = JSON.parse(xhr.responseText); }
              catch { return JSON.stringify({ error: 'FTMS API trả về dữ liệu không hợp lệ' }); }
              const unwrap = (value, depth = 0) => {
                if (depth > 8 || value == null) return [];
                if (Array.isArray(value)) return value;
                if (typeof value === 'string') {
                  try { return unwrap(JSON.parse(value), depth + 1); } catch { return []; }
                }
                if (typeof value !== 'object') return [];
                for (const key of ['data','Data','rows','Rows','items','Items','result','Result']) {
                  const rows = unwrap(value[key], depth + 1);
                  if (rows.length) return rows;
                }
                for (const child of Object.values(value)) {
                  const rows = unwrap(child, depth + 1);
                  if (rows.some(x => x && typeof x === 'object')) return rows;
                }
                return [];
              };
              const pick = (row, ...keys) => {
                for (const key of keys) if (row?.[key] !== undefined && row[key] !== null && row[key] !== '') return row[key];
                return null;
              };
              const statusMap = { 'mới': 0, 'tạo mới': 0, 'new': 0, 'phân công': 1, 'assigned': 1,
                'đang thực hiện': 2, 'in progress': 2, 'hoàn thành': 3, 'completed': 3,
                'tạm ngưng': 4, 'paused': 4, 'đóng': 5, 'đã đóng': 5, 'closed': 5,
                'hủy': 7, 'đã hủy': 7, 'cancelled': 7, 'không xử lý': 8 };
              const statusOf = row => {
                const raw = pick(row, 'status','Status','statusId','StatusId','STATUS_ID','STATUSID','statusID');
                if (typeof raw === 'number') return raw;
                if (raw && typeof raw === 'object') return Number(pick(raw, 'id','Id','value','Value') ?? 0);
                const numeric = Number(raw);
                if (raw !== null && raw !== '' && Number.isFinite(numeric)) return numeric;
                const name = String(pick(row, 'statusName','StatusName','STATUS_NAME','statusText','StatusText', 'status') || '')
                  .trim().toLocaleLowerCase('vi-VN');
                return statusMap[name] ?? null;
              };
              const dateOf = value => {
                if (!value) return null;
                if (typeof value === 'string') {
                  const vi = value.match(/(\d{1,2}):(\d{2})(?::(\d{2}))?\s*-\s*(\d{1,2})\/(\d{1,2})\/(\d{4})/);
                  if (vi) return `${vi[6]}-${vi[5].padStart(2,'0')}-${vi[4].padStart(2,'0')}T${vi[1].padStart(2,'0')}:${vi[2]}:${vi[3] || '00'}+07:00`;
                  const dotNet = value.match(/\/Date\((\d+)(?:[+-]\d+)?\)\//);
                  if (dotNet) return new Date(Number(dotNet[1])).toISOString();
                }
                const parsed = new Date(value);
                return Number.isNaN(parsed.getTime()) ? null : parsed.toISOString();
              };
              const numberOf = value => {
                if (value === null || value === undefined || value === '') return null;
                const parsed = Number(value);
                return Number.isFinite(parsed) ? parsed : null;
              };
              const emailOf = row => {
                const preferred = pick(row, 'customerEmail','CustomerEmail','cusEmail','CusEmail','customerMail','CustomerMail',
                  'emailFrom','EmailFrom','fromEmail','FromEmail','fromAddress','FromAddress','senderEmail','SenderEmail',
                  'sender','Sender','mailFrom','MailFrom','email','Email','contactEmail','ContactEmail');
                const extract = value => String(value || '').match(/[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/ig) || [];
                const acceptable = value => !/ihub\.akabot2@fpt\.com/i.test(value);
                const preferredEmail = extract(preferred).find(acceptable);
                if (preferredEmail) return preferredEmail;
                const ranked = Object.entries(row || {})
                  .flatMap(([key, value]) => extract(value).map(email => ({ email, key })))
                  .filter(item => acceptable(item.email))
                  .sort((a, b) => {
                    const rank = key => /(?:customer|cus|contact)/i.test(key) ? 0 :
                      /(?:from|sender)/i.test(key) ? 1 : /email|mail/i.test(key) ? 2 : 3;
                    return rank(a.key) - rank(b.key);
                  });
                return ranked[0]?.email || null;
              };
              let rows = unwrap(response);
              const pageSize = 1000;
              for (let page = 2; rows.length >= (page - 1) * pageSize && page <= 50; page++) {
                const nextBody = new URLSearchParams({ take: String(pageSize), skip: String((page - 1) * pageSize),
                  page: String(page), pageSize: String(pageSize), search: '', isMyTicket: '', isAkabot: '',
                  strStatus: '', strRegionID: '', linkDeptId: '', alarmType: '0', isSortByDate: '' });
                const nextRequest = new XMLHttpRequest();
                nextRequest.__ftmsCompanionInternal = true;
                nextRequest.open('POST', '/ihub/request/GetListRequestV12', false);
                nextRequest.setRequestHeader('Content-Type', 'application/x-www-form-urlencoded; charset=UTF-8');
                nextRequest.setRequestHeader('X-Requested-With', 'XMLHttpRequest');
                try { nextRequest.send(nextBody.toString()); } catch { break; }
                if (nextRequest.status < 200 || nextRequest.status >= 300) break;
                let nextResponse;
                try { nextResponse = JSON.parse(nextRequest.responseText); } catch { break; }
                const nextRows = unwrap(nextResponse);
                if (!nextRows.length) break;
                rows.push(...nextRows);
                if (nextRows.length < pageSize) break;
              }
              const tickets = rows.map(row => ({
                code: String(pick(row, 'code','Code','requestCode','RequestCode','REQUEST_CODE','REQUESTCODE','requestNo','RequestNo') || '').trim(),
                status: statusOf(row),
                title: pick(row, 'title','Title','subject','Subject','REQUEST_TITLE'),
                createdAt: dateOf(pick(row, 'createDate','CreateDate','createdAt','CreatedAt','CREATE_DATE')),
                updatedAt: dateOf(pick(row, 'updateDate','UpdateDate','updatedAt','UpdatedAt','modifyDate','ModifyDate','lastUpdate','LastUpdate')),
                updatedBy: pick(row, 'updatedBy','UpdatedBy','updateBy','UpdateBy','modifiedBy','ModifiedBy','lastUpdateBy','LastUpdateBy','updateStaffName','UpdateStaffName'),
                assigneeId: numberOf(pick(row, 'staffId','StaffId','agentId','AgentId','ASSIGNEE_ID')),
                assigneeName: pick(row, 'agentName','AgentName','staffName','StaffName','assigneeName','AssigneeName'),
                departmentId: numberOf(pick(row, 'deptId','DeptId','departmentId','DepartmentId','DEPARTMENT_ID')),
                departmentName: pick(row, 'department','Department','departmentName','DepartmentName','deptName','DeptName'),
                slaDeviationMinutes: numberOf(pick(row, 'slaDeviation','SlaDeviation','SLA_DEVIATION','slaDeviationMinutes','SlaDeviationMinutes')),
                slaType: numberOf(pick(row, 'typeSLA','TypeSLA','typeSla','slaType','SlaType','SLA_TYPE')),
                latestEmail: (() => {
                  const sender = emailOf(row);
                  const subject = pick(row, 'emailSubject','EmailSubject','subject','Subject','title','Title','REQUEST_TITLE');
                  const content = pick(row, 'emailContent','EmailContent','contents','Contents','content','Content','description','Description','note','Note','catalogName','CatalogName');
                  const sentAt = dateOf(pick(row, 'emailDate','EmailDate','sendDate','SendDate','sentAt','SentAt','createDate','CreateDate'));
                  if (!sender && !subject && !content) return null;
                  const body = String(content || '').trim() === String(subject || '').trim() ? null : content;
                  return { id: String(pick(row, 'emailHistoryId','EmailHistoryId','emailId','EmailId','id','Id') || ''),
                    sentAt, from: sender, subject, body };
                })()
              })).filter(x => x.code && x.status !== null);

              // FTMS removes closed tickets from the active list and exposes them through the history API.
              // Read recent closures so a Completed -> Closed transition is still detected in real time.
              const closedTickets = [];
              try {
                const formatDate = date => `${String(date.getMonth() + 1).padStart(2, '0')}/${String(date.getDate()).padStart(2, '0')}/${date.getFullYear()}`;
                const today = new Date();
                const yesterday = new Date(today); yesterday.setDate(today.getDate() - 1);
                const historyParams = new URLSearchParams({
                  searchData: JSON.stringify({ search: '', source: '-1', departmentId: '-1', staffId: '-1',
                    serviceTypeGroup: '-1', serviceType: '-1', requestLevel: '-1', status: '6' }),
                  fromDate: formatDate(yesterday), toDate: formatDate(today), typeSearch: '0',
                  take: '1000', skip: '0', page: '1', pageSize: '1000'
                });
                const historyRequest = new XMLHttpRequest();
                historyRequest.__ftmsCompanionInternal = true;
                historyRequest.open('GET', '/ihub/list/GetListHistoryRequestByType?' + historyParams.toString(), false);
                historyRequest.setRequestHeader('X-Requested-With', 'XMLHttpRequest');
                historyRequest.send();
                if (historyRequest.status >= 200 && historyRequest.status < 300) {
                  const historyRows = unwrap(JSON.parse(historyRequest.responseText));
                  for (const row of historyRows) {
                    const code = String(pick(row, 'code','Code','requestCode','RequestCode') || '').trim();
                    if (!code) continue;
                    closedTickets.push({
                      code, status: 5,
                      title: pick(row, 'title','Title','subject','Subject'),
                      createdAt: dateOf(pick(row, 'createDate','CreateDate','createdAt','CreatedAt')),
                      updatedAt: dateOf(pick(row, 'closeDate','CloseDate','updatedAt','UpdatedAt')),
                      updatedBy: pick(row, 'userName','UserName','closedBy','ClosedBy','updatedBy','UpdatedBy'),
                      assigneeId: numberOf(pick(row, 'staffId','StaffId','agentId','AgentId')),
                      assigneeName: pick(row, 'userName','UserName','agentName','AgentName','staffName','StaffName'),
                      departmentId: numberOf(pick(row, 'departmentId','DepartmentId','deptId','DeptId')),
                      departmentName: pick(row, 'departmentName','DepartmentName','department','Department'),
                      slaDeviationMinutes: null, slaType: null
                    });
                  }
                }
              } catch {}

              // The endpoint can repeat a request in overlapping pages. Keep the first row because
              // FTMS sorts the current list before older/secondary representations of the same code.
              const byCode = new Map();
              for (const ticket of tickets) {
                const key = ticket.code.toLocaleUpperCase('vi-VN');
                if (!byCode.has(key)) byCode.set(key, ticket);
              }
              for (const ticket of closedTickets) {
                const key = ticket.code.toLocaleUpperCase('vi-VN');
                if (!byCode.has(key)) byCode.set(key, ticket);
              }
              const allTickets = Array.from(byCode.values());
              return JSON.stringify({ data: allTickets, count: allTickets.length });
            })()
            """;
        var json = await ExecuteJsonStringAsync(script, "tickets", TimeSpan.FromSeconds(45), cancellationToken);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.String)
        {
            using var nested = JsonDocument.Parse(root.GetString() ?? "null");
            root = nested.RootElement.Clone();
        }
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
        {
            var message = error.GetString() ?? "Không thể đọc API FTMS";
            if (message.Contains("401", StringComparison.OrdinalIgnoreCase) || message.Contains("403", StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException(message);
            throw new InvalidOperationException(message);
        }
        return DeserializeTickets(root.GetRawText());
    }

    private async Task<IReadOnlyList<TicketSnapshot>> GetTicketsLegacyAsync(CancellationToken cancellationToken)
    {
        const string script = """
            (() => {
              const visibleCards = Array.from(document.querySelectorAll('#list-grid .card-list'));
              if (visibleCards.length) {
                const statusMap = { 'tạo mới': 0, 'mới': 0, 'phân công': 1, 'đang thực hiện': 2,
                  'hoàn thành': 3, 'tạm ngưng': 4, 'đóng': 5, 'đã đóng': 5, 'hủy': 7, 'không xử lý': 8 };
                const text = (root, selector) => root.querySelector(selector)?.textContent?.trim() || null;
                return JSON.stringify(visibleCards.map(card => {
                  const statusText = (text(card, '.ticket-status') || '').toLocaleLowerCase('vi-VN');
                  const createdText = text(card, '.ticket-create-time');
                  let createdAt = null;
                  const match = createdText?.match(/(\d{2}):(\d{2}):(\d{2})\s*-\s*(\d{2})\/(\d{2})\/(\d{4})/);
                  if (match) createdAt = `${match[6]}-${match[5]}-${match[4]}T${match[1]}:${match[2]}:${match[3]}+07:00`;
                  return { code: text(card, '.ticket-code'), status: statusMap[statusText] ?? 0,
                    title: text(card, '.ticket-title'), createdAt, assigneeName: text(card, '.ticket-handler'),
                    departmentName: text(card, '.ticket-department'), slaDeviationMinutes: null, slaType: null };
                }).filter(x => x.code));
              }
              let result = null;
              try {
                const body = new URLSearchParams({ take: '500', skip: '0', page: '1', pageSize: '500',
                  search: '', isMyTicket: '', isAkabot: '', strStatus: '', strRegionID: '', linkDeptId: '',
                  alarmType: '0', isSortByDate: '' });
                const xhr = new XMLHttpRequest(); xhr.__ftmsCompanionInternal = true; xhr.open('POST', '/ihub/request/GetListRequestV12', false);
                xhr.setRequestHeader('Content-Type', 'application/x-www-form-urlencoded; charset=UTF-8');
                xhr.send(body.toString());
                if (xhr.status >= 200 && xhr.status < 300) result = JSON.parse(xhr.responseText);
              } catch {}
              const findRows = (value, depth = 0) => {
                if (depth > 6 || value == null) return [];
                if (Array.isArray(value)) return value;
                if (typeof value === 'string') {
                  try { return findRows(JSON.parse(value), depth + 1); } catch { return []; }
                }
                if (typeof value !== 'object') return [];
                for (const key of ['data','Data','rows','Rows','items','Items','result','Result']) {
                  const rows = findRows(value[key], depth + 1);
                  if (rows.length) return rows;
                }
                for (const child of Object.values(value)) {
                  const rows = findRows(child, depth + 1);
                  if (rows.length && rows.some(x => x && typeof x === 'object' && ('code' in x || 'Code' in x))) return rows;
                }
                return [];
              };
              let rows = findRows(result);
              const cards = Array.from(document.querySelectorAll('#list-grid .card-list'));
              const hasTicketCode = rows.some(x => x && typeof x === 'object' &&
                (x.code || x.Code || x.requestCode || x.RequestCode || x.REQUEST_CODE || x.REQUESTCODE));
              if (cards.length || !hasTicketCode) {
                const statusMap = { 'tạo mới': 0, 'mới': 0, 'phân công': 1, 'đang thực hiện': 2,
                  'hoàn thành': 3, 'tạm ngưng': 4, 'đóng': 5, 'đã đóng': 5, 'hủy': 7, 'không xử lý': 8 };
                const text = (root, selector) => root.querySelector(selector)?.textContent?.trim() || null;
                rows = cards.map(card => {
                  const statusText = (text(card, '.ticket-status') || '').toLocaleLowerCase('vi-VN');
                  const createdText = text(card, '.ticket-create-time');
                  let createdAt = null;
                  const match = createdText?.match(/(\d{2}):(\d{2}):(\d{2})\s*-\s*(\d{2})\/(\d{2})\/(\d{4})/);
                  if (match) createdAt = `${match[6]}-${match[5]}-${match[4]}T${match[1]}:${match[2]}:${match[3]}+07:00`;
                  return { code: text(card, '.ticket-code'), status: statusMap[statusText] ?? 0,
                    title: text(card, '.ticket-title'), createdAt, assigneeName: text(card, '.ticket-handler'),
                    departmentName: text(card, '.ticket-department'), slaDeviationMinutes: null, slaType: null };
                }).filter(x => x.code);
              }
              return JSON.stringify(rows.map(x => ({ code: x.code || x.Code || x.requestCode || x.RequestCode || x.REQUEST_CODE || x.REQUESTCODE,
                status: Number(x.status ?? x.Status ?? x.statusId ?? x.StatusId ?? x.STATUS_ID ?? 0),
                title: x.title || x.Title, createdAt: x.createDate || x.CreateDate, assigneeId: x.staffId || x.StaffId,
                assigneeName: x.agentName || x.AgentName || x.staffName || x.StaffName,
                departmentId: x.deptId || x.DeptId, departmentName: x.department || x.Department,
                slaDeviationMinutes: x.slaDeviation ?? x.SlaDeviation, slaType: x.typeSLA ?? x.TypeSLA })));
            })()
            """;
        var json = await ExecuteJsonStringAsync(script, "tickets-legacy", TimeSpan.FromSeconds(30), cancellationToken);
        return DeserializeTickets(json);
    }

    public async Task<LatestEmail?> GetLatestEmailAsync(string ticketCode, CancellationToken cancellationToken)
    {
        var code = JsonSerializer.Serialize(ticketCode);
        var script = $$"""
            (() => {
              try {
                const parseMailDate = value => {
                  if (!value) return null;
                  const normalized = String(value).replace(/(\d{1,2})(?:st|nd|rd|th)\b/gi, '$1').trim();
                  const parsed = new Date(normalized);
                  return Number.isNaN(parsed.getTime()) ? null : parsed;
                };
                const quotedOriginal = (body, subject) => {
                  const match = String(body || '').match(/(?:<hr\b[^>]*>|id=["'](?:divRplyFwdMsg|x_divRplyFwdMsg)["'][^>]*>)([\s\S]*)/i);
                  if (!match) return null;
                  const prepared = match[1].replace(/<br\s*\/?>/gi, '\n').replace(/<\/div>|<\/p>/gi, '\n');
                  const text = new DOMParser().parseFromString(prepared, 'text/html').body.textContent
                    .replace(/\u00a0/g, ' ').replace(/[ \t]+/g, ' ').replace(/\n\s*\n+/g, '\n').trim();
                  const fromMatch = text.match(/(?:Từ|Từ|From)\s*:\s*([^\s,;<>]+@[^\s,;<>]+)/i);
                  const sentMatch = text.match(/(?:Đã gửi|Đã gửi|Sent)\s*:\s*([^\n]+)/i);
                  const subjectIndex = text.search(/(?:Chủ đề|Subject)\s*:/i);
                  let content = subjectIndex >= 0 ? text.slice(subjectIndex).replace(/^(?:Chủ đề|Subject)\s*:\s*/i, '').trim() : text;
                  content = content.replace(/^(?:[^\n]*\n){0,2}(?=Dear|Chào|Kính gửi|Nhờ|Hi\b)/i, '').trim();
                  if (!fromMatch || !content) return null;
                  const parsedDate = sentMatch ? parseMailDate(sentMatch[1].trim()) : null;
                  return { from: fromMatch[1], sentAt: parsedDate && !Number.isNaN(parsedDate.getTime()) ? parsedDate.toISOString() : null,
                    subject: subject || null, body: content };
                };
                const unwrapRows = (value, depth = 0) => {
                  if (depth > 6 || value == null) return [];
                  if (Array.isArray(value)) return value;
                  if (typeof value === 'string') {
                    try { return unwrapRows(JSON.parse(value), depth + 1); } catch { return []; }
                  }
                  if (typeof value !== 'object') return [];
                  for (const key of ['Data','data','Rows','rows','Items','items','Result','result']) {
                    const rows = unwrapRows(value[key], depth + 1);
                    if (rows.length) return rows;
                  }
                  return [];
                };
                const readMailFile = fileId => {
                  if (!fileId) return '';
                  const request = new XMLHttpRequest();
                  request.open('GET', '/ihub/email/ReadMailFromFile?fileId=' + encodeURIComponent(fileId), false);
                  try { request.send(); }
                  catch { return ''; }
                  if (request.status < 200 || request.status >= 300) return '';
                  let content = request.responseText || '';
                  try {
                    const parsed = JSON.parse(content);
                    content = typeof parsed === 'string' ? parsed :
                      parsed?.data || parsed?.Data || parsed?.content || parsed?.Content || parsed?.body || parsed?.Body || content;
                  } catch {}
                  return String(content || '');
                };
                const readEmailDetail = emailId => {
                  if (!emailId) return null;
                  const request = new XMLHttpRequest();
                  request.open('GET', '/ihub/email/index/' + encodeURIComponent(emailId), false);
                  try { request.send(); }
                  catch { return null; }
                  if (request.status < 200 || request.status >= 300) return null;
                  const doc = new DOMParser().parseFromString(request.responseText, 'text/html');
                  const value = selector => {
                    const element = doc.querySelector(selector);
                    return String(element?.value || element?.getAttribute?.('value') || element?.textContent || '').trim() || null;
                  };
                  return { from: value('#sender') || value('[name="mail_poster"]'),
                    subject: value('#subject_response') || value('[name="mail_subject"]'),
                    sentAt: value('#sendDate') || value('#sentDate') || value('#createDate') ||
                      value('[name="sendDate"]') || value('[name="sentDate"]') || value('[name="createDate"]') };
                };
                const historyRequest = new XMLHttpRequest();
                historyRequest.open('POST', '/ihub/Email/GetEmailByCode', false);
                historyRequest.setRequestHeader('Content-Type', 'application/json; charset=UTF-8');
                historyRequest.setRequestHeader('X-Requested-With', 'XMLHttpRequest');
                historyRequest.send(JSON.stringify({ code: {{code}} }));
                if (historyRequest.status >= 200 && historyRequest.status < 300) {
                  let history = unwrapRows(JSON.parse(historyRequest.responseText || '[]'));
                  if (history.length) {
                    const dateValue = value => {
                      const fallback = Object.entries(value || {}).find(([key, fieldValue]) =>
                        /(?:date|time|sent|send|created)/i.test(key) && fieldValue)?.[1];
                      const raw = value?.createDate || value?.CreateDate || value?.createdDate || value?.CreatedDate ||
                        value?.sendDate || value?.SendDate || value?.sentDate || value?.SentDate ||
                        value?.emailDate || value?.EmailDate || value?.date || value?.Date || fallback;
                      const parsed = parseMailDate(raw); return parsed?.getTime() || 0;
                    };
                    history.sort((a, b) => dateValue(b) - dateValue(a) || Number(b.id || b.Id || 0) - Number(a.id || a.Id || 0));
                    for (const mail of history) {
                      const emailId = mail.id || mail.Id || mail.emailHistoryId || mail.EmailHistoryId;
                      const fileId = mail.fileId || mail.FileId || mail.FILEID || mail.FILE_ID || mail.emailFileId || mail.EmailFileId;
                      let body = mail.contents || mail.Contents || mail.content || mail.Content || mail.body || mail.Body || '';
                      if (!body || String(body).trim() === String(mail.subject || mail.Subject || '').trim())
                        body = readMailFile(fileId) || body;
                      let sender = mail.customerEmail || mail.CustomerEmail || mail.cusEmail || mail.CusEmail ||
                        mail.emailFrom || mail.EmailFrom || mail.fromEmail || mail.FromEmail ||
                        mail.fromAddress || mail.FromAddress || mail.senderEmail || mail.SenderEmail ||
                        mail.sender || mail.Sender || mail.email || mail.Email || mail.poster || mail.Poster || null;
                      if (!sender) {
                        const candidates = Object.entries(mail).flatMap(([key, value]) => {
                          const emails = String(value || '').match(/[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/ig) || [];
                          return emails.map(email => ({ key, email }));
                        }).filter(item => !/ihub\.akabot2@fpt\.com/i.test(item.email));
                        candidates.sort((a, b) => {
                          const rank = key => /(?:customer|cus|contact)/i.test(key) ? 0 :
                            /(?:from|sender|poster)/i.test(key) ? 1 : /email|mail/i.test(key) ? 2 : 3;
                          return rank(a.key) - rank(b.key);
                        });
                        sender = candidates[0]?.email || null;
                      }
                      const detail = readEmailDetail(emailId);
                      sender = sender || detail?.from || null;
                      const searchable = `${sender || ''} ${body || ''}`;
                      if (/ihub\.akabot2@fpt\.com/i.test(searchable)) continue;
                      const plainBody = new DOMParser().parseFromString(String(body), 'text/html').body.textContent
                        .replace(/\s+/g, ' ').trim().toLocaleLowerCase('vi-VN');
                      const standardReceipt = plainBody.includes('thông tin yêu cầu hỗ trợ') &&
                        plainBody.includes('đã được tiếp nhận') && plainBody.includes('chuyển đến bộ phận');
                      const technicalReceipt = plainBody.includes('thông tin hỗ trợ') &&
                        plainBody.includes('đã được tiếp nhận') && plainBody.includes('kỹ thuật sẽ kiểm tra và phản hồi');
                      const assignmentReceipt = plainBody.includes('kỹ thuật fpt nhận thông tin ycht') &&
                        plainBody.includes('phân công nhân sự xử lý theo rq');
                      if (standardReceipt || technicalReceipt || assignmentReceipt) {
                        const original = quotedOriginal(body, mail.subject || mail.Subject || null);
                        if (original && !/ihub\.akabot2@fpt\.com/i.test(original.from)) {
                          return JSON.stringify({ id: String(mail.id || mail.Id || '') + ':quoted', ...original });
                        }
                        continue;
                      }
                      const fallbackDate = Object.entries(mail).find(([key, fieldValue]) =>
                        /(?:date|time|sent|send|created)/i.test(key) && fieldValue)?.[1];
                      const rawDate = mail.createDate || mail.CreateDate || mail.createdDate || mail.CreatedDate ||
                        mail.sendDate || mail.SendDate || mail.sentDate || mail.SentDate ||
                        mail.emailDate || mail.EmailDate || mail.date || mail.Date || detail?.sentAt || fallbackDate || null;
                      const quoted = (!sender || !rawDate) ? quotedOriginal(body, mail.subject || mail.Subject || detail?.subject || null) : null;
                      sender = sender || quoted?.from || null;
                      const parsedDate = parseMailDate(rawDate);
                      return JSON.stringify({ id: String(emailId || ''),
                        sentAt: parsedDate && !Number.isNaN(parsedDate.getTime()) ? parsedDate.toISOString() : quoted?.sentAt || null,
                        from: sender, subject: mail.subject || mail.Subject || detail?.subject || null, body });
                    }
                  }
                }
                const form = new FormData(); form.append('id', 'C88'); form.append('body', JSON.stringify({ p_strCode: {{code}} }));
                const metaRequest = new XMLHttpRequest(); metaRequest.open('POST', '/ihub/code/code', false); metaRequest.send(form);
                if (metaRequest.status < 200 || metaRequest.status >= 300) return JSON.stringify(null);
                const meta = JSON.parse(metaRequest.responseText);
                const rows = unwrapRows(meta);
                if (!rows.length) return JSON.stringify(null);
                const historyValue = row => {
                  const normalized = Object.fromEntries(Object.entries(row).map(([key, value]) => [key.replace(/[^a-z0-9]/gi, '').toLowerCase(), value]));
                  const rawDate = normalized.lasttimeresponse || normalized.lastresponsetime || normalized.timeresponse ||
                    normalized.responsetime || normalized.senddate || normalized.sentat || normalized.createdate;
                  const parsed = new Date(rawDate || 0).getTime();
                  const rawId = normalized.maxeh || normalized.emailhistoryid || normalized.id || 0;
                  return { time: Number.isNaN(parsed) ? 0 : parsed, id: Number(rawId) || 0 };
                };
                const sortedRows = [...rows].sort((a, b) => {
                  const left = historyValue(a), right = historyValue(b);
                  return right.time - left.time || right.id - left.id;
                });
                for (const row of sortedRows) {
                  const normalized = Object.fromEntries(Object.entries(row).map(([key, value]) => [key.replace(/[^a-z0-9]/gi, '').toLowerCase(), value]));
                  const field = (...names) => {
                    for (const name of names) {
                      const value = normalized[name.replace(/[^a-z0-9]/gi, '').toLowerCase()];
                      if (value !== undefined && value !== null && value !== '') return value;
                    }
                    return null;
                  };
                  const fileId = field('FILEID', 'FILE_ID', 'EMAIL_FILE_ID'); let body = '';
                  if (fileId) body = readMailFile(fileId);
                  const emailId = field('MAX_EH', 'EMAIL_HISTORY_ID', 'ID');
                  const rawSentAt = field('LAST_TIME_RESPONSE', 'LAST_RESPONSE_TIME', 'TIME_RESPONSE', 'RESPONSE_TIME', 'SEND_DATE', 'SENT_AT', 'CREATE_DATE');
                  let sentAt = rawSentAt;
                  if (typeof rawSentAt === 'string') {
                    const match = rawSentAt.match(/(\d{1,2})[:\/](\d{1,2})(?::(\d{1,2}))?\s*(?:-|\s)\s*(\d{1,2})\/(\d{1,2})\/(\d{4})/);
                    if (match) sentAt = `${match[6]}-${match[5].padStart(2,'0')}-${match[4].padStart(2,'0')}T${match[1].padStart(2,'0')}:${match[2].padStart(2,'0')}:${(match[3] || '00').padStart(2,'0')}+07:00`;
                    else { const parsed = new Date(rawSentAt); sentAt = Number.isNaN(parsed.getTime()) ? null : parsed.toISOString(); }
                  }
                  let sender = field('EMAIL_FROM', 'FROM_EMAIL', 'EMAILFROM', 'FROM_ADDRESS', 'FROMADDRESS',
                    'SENDER_EMAIL', 'SENDER', 'CUSTOMER_EMAIL', 'CUSTOMEREMAIL', 'CUS_EMAIL', 'CUSEMAIL',
                    'CONTACT_EMAIL', 'CONTACTEMAIL', 'POSTER_EMAIL', 'POSTER', 'FROM');
                  if (!sender) {
                    const candidates = Object.entries(row).flatMap(([key, value]) => {
                      const emails = String(value || '').match(/[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/ig) || [];
                      return emails.map(email => ({ key, email }));
                    }).filter(item => !/ihub\.akabot2@fpt\.com|fti\.support@fpt\.com/i.test(item.email));
                    candidates.sort((a, b) => {
                      const rank = key => /(?:customer|cus|contact)/i.test(key) ? 0 :
                        /(?:from|sender|poster)/i.test(key) ? 1 : /email|mail/i.test(key) ? 2 : 3;
                      return rank(a.key) - rank(b.key);
                    });
                    sender = candidates[0]?.email || null;
                  }
                  if (!sender && body) {
                    const latestPart = body.split(/<hr\b|id=["'](?:divRplyFwdMsg|x_divRplyFwdMsg|appendonsend)/i)[0];
                    const emails = latestPart.match(/[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/ig);
                    sender = emails?.at(-1) || null;
                  }
                  const detail = readEmailDetail(emailId);
                  sender = sender || detail?.from || null;
                  const plainBody = new DOMParser().parseFromString(String(body || ''), 'text/html').body.textContent
                    .replace(/\s+/g, ' ').trim().toLocaleLowerCase('vi-VN');
                  const standardReceipt = plainBody.includes('thông tin yêu cầu hỗ trợ') &&
                    plainBody.includes('đã được tiếp nhận') && plainBody.includes('chuyển đến bộ phận');
                  const technicalReceipt = plainBody.includes('thông tin hỗ trợ') &&
                    plainBody.includes('đã được tiếp nhận') && plainBody.includes('kỹ thuật sẽ kiểm tra và phản hồi');
                  const assignmentReceipt = plainBody.includes('kỹ thuật fpt nhận thông tin ycht') &&
                    plainBody.includes('phân công nhân sự xử lý theo rq');
                  const isExcluded = String(sender || '').trim().toLowerCase() === 'ihub.akabot2@fpt.com' ||
                    /ihub\.akabot2@fpt\.com/i.test(body || '') || standardReceipt || technicalReceipt || assignmentReceipt;
                  if (isExcluded) {
                    const original = quotedOriginal(body, field('SUBJECT', 'EMAIL_SUBJECT', 'TITLE'));
                    if (original && !/ihub\.akabot2@fpt\.com/i.test(original.from)) {
                      return JSON.stringify({ id: String(field('MAX_EH', 'EMAIL_HISTORY_ID', 'ID') || fileId || '') + ':quoted', ...original });
                    }
                    continue;
                  }
                  const quoted = (!sender || !sentAt) ? quotedOriginal(body, field('SUBJECT', 'EMAIL_SUBJECT', 'TITLE') || detail?.subject || null) : null;
                  sender = sender || quoted?.from || null;
                  sentAt = sentAt || detail?.sentAt || quoted?.sentAt || null;
                  return JSON.stringify({ id: String(emailId || fileId || ''), sentAt,
                    from: sender, subject: field('SUBJECT', 'EMAIL_SUBJECT', 'TITLE') || detail?.subject || null, body });
                }
                return JSON.stringify(null);
              } catch {
                return JSON.stringify(null);
              }
            })()
            """;
        var json = await ExecuteJsonStringAsync(script, $"email:{ticketCode}", TimeSpan.FromSeconds(20), cancellationToken);
        return DeserializeLatestEmail(json);
    }

    public async Task<StatusHistoryEntry?> GetLatestStatusHistoryAsync(string ticketCode, TicketStatus status, CancellationToken cancellationToken)
    {
        var code = JsonSerializer.Serialize(ticketCode);
        var route = ticketCode.StartsWith("CA", StringComparison.OrdinalIgnoreCase) || ticketCode.StartsWith("AL", StringComparison.OrdinalIgnoreCase)
            ? "case" : "request";
        var expectedStatus = (int)status;
        var script = $$"""
            (() => {
              try {
                const xhr = new XMLHttpRequest();
                xhr.open('GET', '/ihub/{{route}}/edit/' + encodeURIComponent({{code}}), false);
                xhr.send();
                if (xhr.status < 200 || xhr.status >= 300) return JSON.stringify(null);
                const doc = new DOMParser().parseFromString(xhr.responseText, 'text/html');
                const normalize = value => (value || '').replace(/\s+/g, ' ').trim();
                const statusMap = { 'mới': 0, 'tạo mới': 0, 'phân công': 1, 'đang thực hiện': 2,
                  'hoàn thành': 3, 'tạm ngưng': 4, 'đóng': 5, 'đã đóng': 5, 'hủy': 7, 'đã hủy': 7,
                  'không xử lý': 8 };
                const entries = Array.from(doc.querySelectorAll('li h6')).map(heading => {
                  const item = heading.parentElement;
                  const paragraphs = Array.from(item?.querySelectorAll('p') || []).map(p => normalize(p.textContent));
                  const dateText = paragraphs.find(x => x.startsWith('Ngày:'))?.replace(/^Ngày:\s*/, '') || '';
                  const actor = paragraphs.find(x => x.startsWith('Thực hiện:'))?.replace(/^Thực hiện:\s*/, '') || null;
                  let occurredAt = null;
                  const match = dateText.match(/(\d{1,2}):(\d{2})\s*-\s*(\d{1,2})\/(\d{1,2})\/(\d{4})/);
                  if (match) occurredAt = `${match[5]}-${match[4].padStart(2,'0')}-${match[3].padStart(2,'0')}T${match[1].padStart(2,'0')}:${match[2]}:00+07:00`;
                  const statusText = normalize(heading.textContent);
                  return { status: statusMap[statusText.toLocaleLowerCase('vi-VN')], occurredAt, actor };
                }).filter(x => x.occurredAt && x.status === {{expectedStatus}});
                return JSON.stringify(entries.at(-1) || null);
              } catch { return JSON.stringify(null); }
            })()
            """;
        var json = await ExecuteJsonStringAsync(script, $"status-history:{ticketCode}", TimeSpan.FromSeconds(20), cancellationToken);
        if (string.IsNullOrWhiteSpace(json) || json == "null") return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                using var nested = JsonDocument.Parse(root.GetString() ?? "null");
                root = nested.RootElement.Clone();
            }
            if (root.ValueKind != JsonValueKind.Object) return null;
            var actor = root.TryGetProperty("actor", out var actorValue) ? actorValue.GetString() : null;
            var occurredText = root.TryGetProperty("occurredAt", out var occurredValue) ? occurredValue.GetString() : null;
            DateTimeOffset? occurredAt = DateTimeOffset.TryParse(occurredText, out var parsed) ? parsed : null;
            return new StatusHistoryEntry(status, occurredAt, actor);
        }
        catch (JsonException) { return null; }
    }

    public async Task BeginLoginRecoveryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DateTimeOffset.Now - _lastLoginRecoveryAt < TimeSpan.FromSeconds(10)) return;
        _lastLoginRecoveryAt = DateTimeOffset.Now;
        await webView.Dispatcher.InvokeAsync(() =>
        {
            var currentUrl = webView.Source?.AbsoluteUri ?? string.Empty;
            var onLoginPage = currentUrl.Contains("login", StringComparison.OrdinalIgnoreCase) ||
                currentUrl.Contains("adfs", StringComparison.OrdinalIgnoreCase) ||
                currentUrl.Contains("/id/", StringComparison.OrdinalIgnoreCase);
            if (!onLoginPage) webView.Source = new Uri(ftmsUrl);
        });
    }

    private async Task<string> ExecuteJsonStringAsync(string script, string operationName, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var operation = await webView.Dispatcher.InvokeAsync(() => webView.ExecuteScriptAsync(script));
            var raw = await operation.WaitAsync(timeout, cancellationToken);
            try { return JsonSerializer.Deserialize<string>(raw) ?? "null"; }
            catch (JsonException) { return raw; }
        }
        catch (TimeoutException)
        {
            _ = webView.Dispatcher.InvokeAsync(() => webView.Reload());
            throw new TimeoutException($"FTMS API timeout: {operationName}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static LatestEmail? DeserializeLatestEmail(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null") return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String) return DeserializeLatestEmail(root.GetString() ?? "null");
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? ReadString(string name) => root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
                ? value.ToString() : null;
            var sentAtText = ReadString("sentAt");
            DateTimeOffset? sentAt = DateTimeOffset.TryParse(sentAtText, out var parsed) ? parsed : null;
            return new LatestEmail(ReadString("id"), sentAt, ReadString("from"), ReadString("subject"), ReadString("body"));
        }
        catch (JsonException) { return null; }
    }

    private static IReadOnlyList<TicketSnapshot> DeserializeTickets(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null") return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String) return DeserializeTickets(root.GetString() ?? "null");
            if (root.ValueKind == JsonValueKind.Array)
                return JsonSerializer.Deserialize<List<TicketSnapshot>>(root.GetRawText(), JsonOptions) ?? [];
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("data", out var data) || root.TryGetProperty("Data", out data))
                    return data.ValueKind == JsonValueKind.Array
                        ? JsonSerializer.Deserialize<List<TicketSnapshot>>(data.GetRawText(), JsonOptions) ?? []
                        : [];
            }
        }
        catch (JsonException) { }
        return [];
    }
}
