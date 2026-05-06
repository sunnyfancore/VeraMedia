using System.Security.Cryptography;
using Markdig;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VeraMedia.Api.Contracts;
using VeraMedia.Api.Data;
using VeraMedia.Api.Models;
using VeraMedia.Api.Services;

namespace VeraMedia.Api.Controllers;

[ApiController]
public sealed class ShareController(AppDbContext db) : ControllerBase
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    [Authorize]
    [HttpPost("api/shares")]
    public async Task<ActionResult<CreateShareResponse>> Create(CreateShareRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();

        var project = await db.ContentProjects.FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (project is null)
        {
            project = new ContentProject { UserId = userId, Title = "分享文章" };
            db.ContentProjects.Add(project);
            await db.SaveChangesAsync(cancellationToken);
        }

        var article = new Article
        {
            ProjectId = project.Id,
            Title = request.Title,
            Body = request.Body,
            Platform = "wechat"
        };
        db.Articles.Add(article);

        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');

        var share = new ArticleShare
        {
            Article = article,
            Token = token,
            ExpiresAt = ParseExpiry(request.ExpiresIn)
        };
        db.ArticleShares.Add(share);
        await db.SaveChangesAsync(cancellationToken);

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        return Ok(new CreateShareResponse(article.Id, $"{baseUrl}/s/{token}", token, share.ExpiresAt));
    }

    [AllowAnonymous]
    [HttpGet("s/{token}")]
    public async Task<IActionResult> View(string token, CancellationToken cancellationToken)
    {
        var share = await db.ArticleShares
            .Include(x => x.Article)
            .FirstOrDefaultAsync(x => x.Token == token, cancellationToken);

        if (share is null)
            return Content(BuildErrorPage("链接不存在", "您访问的分享链接无效，请确认链接是否正确。"), "text/html");

        if (share.ExpiresAt.HasValue && share.ExpiresAt.Value < DateTime.UtcNow)
            return Content(BuildErrorPage("链接已过期", "该分享链接已超过有效期，请联系作者重新分享。"), "text/html");

        var article = share.Article!;
        var cleanBody = ArticleMarkdownImageComposer.StripImagePromptComments(article.Body);
        var htmlBody = Markdown.ToHtml(cleanBody, Pipeline);

        return Content(BuildArticlePage(article.Title, htmlBody), "text/html");
    }

    private static DateTime? ParseExpiry(string expiresIn) => expiresIn switch
    {
        "1h" => DateTime.UtcNow.AddHours(1),
        "24h" => DateTime.UtcNow.AddHours(24),
        "7d" => DateTime.UtcNow.AddDays(7),
        "30d" => DateTime.UtcNow.AddDays(30),
        "permanent" => null,
        _ => DateTime.UtcNow.AddHours(24)
    };

    private static string BuildArticlePage(string title, string htmlBody)
    {
        var safeTitle = System.Net.WebUtility.HtmlEncode(title);
        return $"""
            <!DOCTYPE html>
            <html lang="zh-CN">
            <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>{safeTitle}</title>
            <meta property="og:title" content="{safeTitle}" />
            <meta property="og:type" content="article" />
            <style>{ArticleCss}</style>
            </head>
            <body>
            <div class="page">
              <div class="toolbar">
                <button onclick="copyTitle()" id="btn-title">复制标题</button>
                <button onclick="copyBody()" id="btn-body">复制正文</button>
              </div>
              <article class="card">{htmlBody}</article>
              <div class="footer">由 VeraMedia 内容运营助手生成</div>
            </div>
            <script>{CopyScript}</script>
            </body>
            </html>
            """;
    }

    private static string BuildErrorPage(string title, string message)
    {
        var safeTitle = System.Net.WebUtility.HtmlEncode(title);
        var safeMessage = System.Net.WebUtility.HtmlEncode(message);
        return $"""
            <!DOCTYPE html>
            <html lang="zh-CN">
            <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>{safeTitle}</title>
            <style>{ErrorCss}</style>
            </head>
            <body>
            <div class="box">
              <h1>{safeTitle}</h1>
              <p>{safeMessage}</p>
            </div>
            </body>
            </html>
            """;
    }

    private const string ArticleCss = """
        *{margin:0;padding:0;box-sizing:border-box}
        body{font-family:-apple-system,"PingFang SC","Microsoft YaHei",sans-serif;color:#1d2939;background:#f5f6f8;line-height:1.8}
        .page{max-width:960px;margin:0 auto;padding:40px 24px 80px}
        .toolbar{display:flex;gap:8px;margin-bottom:16px}
        .toolbar button{padding:6px 16px;border-radius:20px;border:1px solid #dfe5ef;background:#fff;color:#344054;font-size:13px;cursor:pointer;transition:all .15s}
        .toolbar button:hover{border-color:#1677ff;color:#1677ff}
        .toolbar button.copied{background:#12b76a;border-color:#12b76a;color:#fff}
        .card{background:#fff;border-radius:12px;padding:48px 56px;box-shadow:0 1px 4px rgba(0,0,0,0.06)}
        h1{font-size:26px;font-weight:800;line-height:1.4;margin-bottom:28px;color:#0f172a}
        h2{font-size:20px;font-weight:700;margin:32px 0 14px;color:#0f172a;padding-left:12px;border-left:3px solid #1677ff}
        h3{font-size:17px;font-weight:700;margin:24px 0 10px;color:#344054}
        p{margin:12px 0;font-size:16px;line-height:2;color:#344054}
        img{max-width:100%;height:auto;border-radius:8px;margin:16px 0}
        blockquote{margin:16px 0;padding:14px 20px;background:#f0f5ff;border-left:3px solid #1677ff;border-radius:0 8px 8px 0;color:#475467}
        blockquote p:last-child{margin-bottom:0}
        ul,ol{margin:12px 0;padding-left:24px}
        li{margin:6px 0;font-size:16px;line-height:1.8}
        hr{border:none;border-top:1px solid #e4eaf2;margin:28px 0}
        table{width:100%;border-collapse:collapse;margin:16px 0;font-size:14px}
        th,td{border:1px solid #e4eaf2;padding:10px 14px;text-align:left}
        th{background:#f7f8fa;font-weight:700}
        code{background:#f2f4f7;padding:2px 6px;border-radius:4px;font-size:14px}
        pre{background:#f7f8fa;padding:16px;border-radius:8px;overflow-x:auto;margin:16px 0}
        pre code{background:none;padding:0}
        .footer{text-align:center;padding:24px 0;color:#98a2b3;font-size:12px}
        @media(max-width:640px){.card{padding:28px 20px;border-radius:0}.page{padding:0 0 40px}h1{font-size:22px}}
        """;

    private const string CopyScript = """
        (function(){
          var T = document.title;
          function done(btn){btn.textContent='已复制';btn.classList.add('copied');setTimeout(function(){btn.textContent=btn.id==='btn-title'?'复制标题':'复制正文';btn.classList.remove('copied')},2000)}
          window.copyTitle=function(){navigator.clipboard.writeText(T).then(function(){done(document.getElementById('btn-title'))})};
          window.copyBody=function(){var c=document.querySelector('.card');var h=c.querySelector('h1');var html,plain;if(h){var r=document.createRange();r.selectNodeContents(c);r.setStartAfter(h);html=r.toString()?c.innerHTML:r.toString();var d=document.createElement('div');d.innerHTML=c.innerHTML;d.removeChild(d.querySelector('h1'));html=d.innerHTML;plain=d.innerText}else{html=c.innerHTML;plain=c.innerText}navigator.clipboard.write([new ClipboardItem({'text/html':new Blob([html],{type:'text/html'}),'text/plain':new Blob([plain],{type:'text/plain'})})]).then(function(){done(document.getElementById('btn-body'))})};
        })();
        """;

    private const string ErrorCss = """
        *{margin:0;padding:0;box-sizing:border-box}
        body{font-family:-apple-system,"PingFang SC","Microsoft YaHei",sans-serif;color:#1d2939;background:#f5f6f8;min-height:100vh;display:grid;place-items:center}
        .box{text-align:center;padding:60px 40px;background:#fff;border-radius:16px;box-shadow:0 1px 4px rgba(0,0,0,0.06);max-width:420px}
        h1{font-size:22px;font-weight:800;margin-bottom:12px;color:#0f172a}
        p{font-size:15px;color:#667085;line-height:1.6}
        """;
}
